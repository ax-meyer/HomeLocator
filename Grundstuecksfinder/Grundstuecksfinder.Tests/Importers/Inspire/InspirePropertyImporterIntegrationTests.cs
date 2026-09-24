using System.Net;
using System.Text;
using FakeItEasy;
using FluentAssertions;
using Grundstuecksfinder.Data;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services.Importers;
using Grundstuecksfinder.Services.Importers.Inspire;
using Grundstuecksfinder.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

/// <summary>
/// End to end through orchestrator, importer and bulk writer against Postgres: what lands in
/// Properties/ImportLogs, and that a failed import leaves the previous data and is retried.
/// </summary>
[Collection("Postgres")]
public sealed class InspirePropertyImporterIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string Source = "sh-test";
    private const string DatasetName = "sh-alkis-test";

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static InspireSourceOptions BuildOptions() => new()
    {
        Source = Source,
        DatasetName = DatasetName,
        ParcelWfsUrl = FakeWfsServer.ParcelUrl,
        AddressWfsUrl = FakeWfsServer.AddressUrl,
        Crs = "urn:ogc:def:crs:EPSG::25832",
        BoundingBox = new InspireBoundingBox { MinX = 0, MinY = 0, MaxX = 100, MaxY = 100 },
        TileSizeMeters = 100,
        MinTileSizeMeters = 1,
        PageSize = 1000,
        MaxAttempts = 2,
        RetryBaseDelaySeconds = 0,
        MaxRetryDelaySeconds = 0,
        MinRequestIntervalSeconds = 0,
    };

    private static FakeWfsServer TwoParcelServer()
    {
        var server = new FakeWfsServer();
        server.Parcels.Add(new FakeParcel("P1", 0, 0, 10, 10, 1250.5));
        server.Parcels.Add(new FakeParcel("P2", 20, 0, 30, 10, 840));
        server.Addresses.Add(new FakeAddress("A1", 5, 5, "Am Kirchhof", "12"));
        server.Addresses.Add(new FakeAddress("A2", 25, 5, "Dorfstraße", "4"));
        return server;
    }

    private ImportOrchestrator BuildOrchestrator(FakeWfsServer server, Action<InspireSourceOptions>? tweak = null)
    {
        var options = BuildOptions();
        tweak?.Invoke(options);

        var httpFactory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => httpFactory.CreateClient(A<string>._)).ReturnsLazily(() => new HttpClient(server));

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var importer = new InspirePropertyImporter(NullLogger<InspirePropertyImporter>.Instance, httpFactory, options);
        return new ImportOrchestrator([importer], NullLogger<ImportOrchestrator>.Instance, scopeFactory,
            new PropertyBulkWriter(fixture.DataSource, batchSize: 1));
    }

    [Fact]
    public async Task CheckAndImportAsync_ValidData_ImportsSpatiallyJoinedRows()
    {
        await BuildOrchestrator(TwoParcelServer()).CheckAndImportAsync(TestContext.Current.CancellationToken);

        await using var context = fixture.CreateContext();
        var properties = await context.Properties.ToListAsync(TestContext.Current.CancellationToken);
        properties.Should().HaveCount(2).And.OnlyContain(p => p.Source == Source);
        properties.Single(p => p.Str == "Am Kirchhof").FlaecheAmtl.Should().Be(1250.5);
        properties.Single(p => p.Str == "Dorfstraße").FlaecheAmtl.Should().Be(840);
    }

    [Fact]
    public async Task CheckAndImportAsync_ValidData_CompletesOneRunForTheWholeState()
    {
        await BuildOrchestrator(TwoParcelServer()).CheckAndImportAsync(TestContext.Current.CancellationToken);

        await using var context = fixture.CreateContext();
        var run = (await context.ImportRuns.ToListAsync(TestContext.Current.CancellationToken)).Should().ContainSingle().Subject;
        run.Source.Should().Be(Source);
        run.Fingerprint.Should().StartWith($"{DatasetName}/statewide/2:2:", "the version is parcel hits, address hits and the month");
        run.RecordCount.Should().Be(2);
        run.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task CheckAndImportAsync_TileSkipped_CompletesAndRecordsItOnTheImportLog()
    {
        // Four tiles, one parcel each; the address service is permanently broken for one of them.
        // The import keeps the other three rather than discarding the run, and says so on the log.
        var server = TwoParcelServer();
        server.Parcels.Add(new FakeParcel("P3", 60, 0, 70, 10, 500));
        server.Addresses.Add(new FakeAddress("A3", 65, 5, "Waldweg", "7"));
        server.Interceptor = (uri, _) => uri.ToString().StartsWith(FakeWfsServer.AddressUrl, StringComparison.Ordinal)
                                         && uri.Query.Contains("bbox=50,0", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : null;
        void TolerantTiles(InspireSourceOptions o)
        {
            o.TileSizeMeters = 50;
            o.MinCompleteness = 0.5; // 2 of 3 addresses; the tolerance itself is tested elsewhere
        }

        await BuildOrchestrator(server, TolerantTiles).CheckAndImportAsync(TestContext.Current.CancellationToken);

        await using var context = fixture.CreateContext();
        (await context.Properties.Select(p => p.Str).ToListAsync(TestContext.Current.CancellationToken))
            .Should().BeEquivalentTo(["Am Kirchhof", "Dorfstraße"], "the broken tile's address is missing, the rest is imported");
        var run = (await context.ImportRuns.ToListAsync(TestContext.Current.CancellationToken)).Should().ContainSingle().Subject;
        run.CompletedAt.Should().NotBeNull("a tolerated hole is still a completed import");
        run.Error.Should().BeNull();
        run.SkippedParts.Should().Be(1, "so the import health check can report the hole");
    }

    [Fact]
    public async Task CheckAndImportAsync_AlreadyCompleted_SkipsFetch()
    {
        var server = TwoParcelServer();
        await BuildOrchestrator(server).CheckAndImportAsync(TestContext.Current.CancellationToken);
        server.Requests.Clear();

        await BuildOrchestrator(server).CheckAndImportAsync(TestContext.Current.CancellationToken);

        server.GetFeatureRequests(FakeWfsServer.ParcelUrl).Should().BeEmpty("the same version was already imported");
    }

    [Fact]
    public async Task CheckAndImportAsync_FailedImport_KeepsPreviousRowsAndRetriesNextRun()
    {
        await using (var context = fixture.CreateContext())
        {
            var old = ImportSeed.Completed(Source, ImportSeed.Epoch, recordCount: 1, fingerprint: "old");
            context.SourceStates.Add(ImportSeed.Serving(old));
            context.Properties.Add(new Property { Str = "Alte Straße", Hnr = "1", FlaecheAmtl = 1, Source = Source, ImportRun = old });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // First run: two tiles; the first one's rows are staged, then the address service goes
        // down for the second tile.
        var server = TwoParcelServer();
        server.Parcels.Add(new FakeParcel("P3", 60, 0, 70, 10, 500));
        server.Addresses.Add(new FakeAddress("A3", 65, 5, "Waldweg", "7"));
        server.Interceptor = (uri, _) => uri.ToString().StartsWith(FakeWfsServer.AddressUrl, StringComparison.Ordinal)
                                         && uri.Query.Contains("bbox=50")
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : null;
        void TwoTiles(InspireSourceOptions o) => o.TileSizeMeters = 50;
        await BuildOrchestrator(server, TwoTiles).CheckAndImportAsync(TestContext.Current.CancellationToken);

        await using (var context = fixture.CreateContext())
        {
            (await context.Properties.Select(p => p.Str).ToListAsync(TestContext.Current.CancellationToken))
                .Should().Equal(["Alte Straße"], "a failed import must not replace the previous data");
            var failed = await context.ImportRuns.SingleAsync(r => r.Fingerprint != "old", TestContext.Current.CancellationToken);
            failed.CompletedAt.Should().BeNull();
            failed.FailedAt.Should().NotBeNull();
        }
        await AssertStagingEmptyAsync();

        // Next run: the service is back; the same version is retried as a new run.
        server.Interceptor = null;
        await BuildOrchestrator(server, TwoTiles).CheckAndImportAsync(TestContext.Current.CancellationToken);

        await using (var context = fixture.CreateContext())
        {
            (await context.Properties.Select(p => p.Str).ToListAsync(TestContext.Current.CancellationToken))
                .Should().BeEquivalentTo(["Am Kirchhof", "Dorfstraße", "Waldweg"]);
            var runs = await context.ImportRuns.Where(r => r.Fingerprint != "old").OrderBy(r => r.Id).ToListAsync(TestContext.Current.CancellationToken);
            runs.Should().HaveCount(2).And.ContainSingle(r => r.CompletedAt != null);
        }
    }

    [Fact]
    public async Task CheckAndImportAsync_HitsRequestFails_DoesNotThrow()
    {
        var server = TwoParcelServer();
        server.Interceptor = (uri, _) => uri.Query.Contains("resultType=hits")
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("", Encoding.UTF8) }
            : null;

        var act = () => BuildOrchestrator(server).CheckAndImportAsync(TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    private async Task AssertStagingEmptyAsync()
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM \"PropertyStaging\"", conn);
        ((long)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).Should().Be(0, "a failed import's staged rows are discarded");
    }
}

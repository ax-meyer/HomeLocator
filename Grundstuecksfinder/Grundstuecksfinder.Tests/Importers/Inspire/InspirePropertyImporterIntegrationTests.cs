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
using Xunit;

namespace Grundstuecksfinder.Tests.Importers.Inspire;

[Collection("Postgres")]
public sealed class InspirePropertyImporterIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    // A single 10x10 bounding box with a 100-unit tile covers the whole box in exactly one
    // tile, so the parcel/address URLs below are the only ones the importer ever requests.
    private const string ParcelWfsUrl = "http://fake/parcels";
    private const string AddressWfsUrl = "http://fake/addresses";
    private const string Crs = "http://www.opengis.net/def/crs/epsg/0/25832";
    private const string DatasetName = "sh-alkis-test";

    private const string ParcelHitsUrl =
        $"{ParcelWfsUrl}?service=WFS&version=2.0.0&request=GetFeature&typenames=cp%3ACadastralParcel&resultType=hits";
    private const string AddressHitsUrl =
        $"{AddressWfsUrl}?service=WFS&version=2.0.0&request=GetFeature&typenames=ad%3AAddress&resultType=hits";
    private const string ParcelFetchUrl =
        $"{ParcelWfsUrl}?service=WFS&version=2.0.0&request=GetFeature&typenames=cp%3ACadastralParcel" +
        "&bbox=0,0,10,10,http%3A%2F%2Fwww.opengis.net%2Fdef%2Fcrs%2Fepsg%2F0%2F25832&srsName=http%3A%2F%2Fwww.opengis.net%2Fdef%2Fcrs%2Fepsg%2F0%2F25832&count=2000&startIndex=0";
    private const string AddressFetchUrl =
        $"{AddressWfsUrl}?service=WFS&version=2.0.0&request=GetFeature&typenames=ad%3AAddress" +
        "&bbox=0,0,10,10,http%3A%2F%2Fwww.opengis.net%2Fdef%2Fcrs%2Fepsg%2F0%2F25832&srsName=http%3A%2F%2Fwww.opengis.net%2Fdef%2Fcrs%2Fepsg%2F0%2F25832&count=2000&startIndex=0" +
        "&resolve=local&resolvedepth=2";

    private const string VersionTimestamp = "2:4"; // "{parcelHits}:{addressHits}" from DiscoverAsync

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static InspireSourceOptions BuildOptions() => new()
    {
        Source = "sh-test",
        DatasetName = DatasetName,
        ParcelWfsUrl = ParcelWfsUrl,
        AddressWfsUrl = AddressWfsUrl,
        Crs = Crs,
        BoundingBox = new InspireBoundingBox { MinX = 0, MinY = 0, MaxX = 10, MaxY = 10 },
        TileSizeMeters = 100,
        PageSize = 2000,
    };

    private ImportOrchestrator BuildOrchestrator(FakeHttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        var httpFactory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => httpFactory.CreateClient(A<string>._)).Returns(http);

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var importer = new InspirePropertyImporter(NullLogger<InspirePropertyImporter>.Instance, httpFactory, BuildOptions());
        var bulkWriter = new PropertyBulkWriter(fixture.DataSource);

        return new ImportOrchestrator([importer], NullLogger<ImportOrchestrator>.Instance, scopeFactory, bulkWriter);
    }

    private static string ReadFixture(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Importers", "Inspire", "TestData", fileName));

    private static HttpResponseMessage HitsResponse(long numberMatched) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $"<wfs:FeatureCollection xmlns:wfs=\"http://www.opengis.net/wfs/2.0\" numberMatched=\"{numberMatched}\"/>",
            Encoding.UTF8, "text/xml"),
    };

    private static void AddHappyPathRoutes(FakeHttpMessageHandler handler, Action? onFetch = null)
    {
        handler.AddRoute(ParcelHitsUrl, () => HitsResponse(2));
        handler.AddRoute(AddressHitsUrl, () => HitsResponse(4));
        handler.AddRoute(ParcelFetchUrl, () =>
        {
            onFetch?.Invoke();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ReadFixture("cadastral_parcels.gml"), Encoding.UTF8, "text/xml"),
            };
        });
        handler.AddRoute(AddressFetchUrl, () =>
        {
            onFetch?.Invoke();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ReadFixture("addresses.gml"), Encoding.UTF8, "text/xml"),
            };
        });
    }

    [Fact]
    public async Task CheckAndImportAsync_ValidData_ImportsSpatiallyJoinedRows()
    {
        var handler = new FakeHttpMessageHandler();
        AddHappyPathRoutes(handler);

        var orchestrator = BuildOrchestrator(handler);
        await orchestrator.CheckAndImportAsync(TestContext.Current.CancellationToken);

        await using var context = fixture.CreateContext();
        var properties = await context.Properties.ToListAsync(TestContext.Current.CancellationToken);

        // Address_3_dangling has no containing parcel and Address_4_nopoint has no geometry at
        // all, so only the two properly-joined addresses should make it through.
        properties.Should().HaveCount(2);
        properties.Should().OnlyContain(p => p.Source == "sh-test");

        var kirchhof = properties.Single(p => p.Str == "Am Kirchhof");
        kirchhof.Hnr.Should().Be("12");
        kirchhof.Plz.Should().Be("24649");
        kirchhof.FlaecheAmtl.Should().Be(1250.5);

        var dorfstrasse = properties.Single(p => p.Str == "Dorfstraße");
        dorfstrasse.Hnr.Should().Be("4");
        dorfstrasse.HnrZus.Should().Be("a");
        dorfstrasse.Plz.Should().Be("24601");
        dorfstrasse.FlaecheAmtl.Should().Be(840.0);
    }

    [Fact]
    public async Task CheckAndImportAsync_ValidData_CreatesOneImportLogForTheWholeState()
    {
        var handler = new FakeHttpMessageHandler();
        AddHappyPathRoutes(handler);

        var orchestrator = BuildOrchestrator(handler);
        await orchestrator.CheckAndImportAsync(TestContext.Current.CancellationToken);

        await using var context = fixture.CreateContext();
        var logs = await context.ImportLogs.ToListAsync(TestContext.Current.CancellationToken);

        // Exactly one ImportLog for the whole run (not one per tile) — PropertyBulkWriter
        // deletes all of a source's rows per call, so multiple candidates would be destructive.
        logs.Should().ContainSingle();
        var log = logs[0];
        log.Source.Should().Be("sh-test");
        log.DatasetName.Should().Be(DatasetName);
        log.FileName.Should().Be("statewide");
        log.FileTimestamp.Should().Be(VersionTimestamp);
        log.RecordCount.Should().Be(2);
    }

    [Fact]
    public async Task CheckAndImportAsync_AlreadyImported_SkipsFetch()
    {
        await using (var context = fixture.CreateContext())
        {
            context.ImportLogs.Add(new ImportLog
            {
                Source = "sh-test",
                DatasetName = DatasetName,
                FileName = "statewide",
                FileTimestamp = VersionTimestamp,
                ImportedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                RecordCount = 2,
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var fetchCalled = false;
        var handler = new FakeHttpMessageHandler();
        AddHappyPathRoutes(handler, onFetch: () => fetchCalled = true);

        var orchestrator = BuildOrchestrator(handler);
        await orchestrator.CheckAndImportAsync(TestContext.Current.CancellationToken);

        fetchCalled.Should().BeFalse("GetFeature should not be requested when the hits-based version was already imported");
    }

    [Fact]
    public async Task CheckAndImportAsync_HitsRequestFails_DoesNotThrow()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(ParcelHitsUrl, () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        handler.AddRoute(AddressHitsUrl, () => HitsResponse(4));

        var orchestrator = BuildOrchestrator(handler);
        var act = () => orchestrator.CheckAndImportAsync();

        await act.Should().NotThrowAsync();
    }
}

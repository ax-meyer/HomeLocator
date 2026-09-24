using System.Runtime.CompilerServices;
using FluentAssertions;
using Grundstuecksfinder.Data;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services.Importers;
using Grundstuecksfinder.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Grundstuecksfinder.Tests.Importers;

/// <summary>
/// Regression coverage for the generic multi-source orchestrator: re-importing one source
/// must never touch another source's rows, dedup must be scoped per source, and one
/// importer failing must not stop the others.
/// </summary>
[Collection("Postgres")]
public sealed class ImportOrchestratorTests(PostgresFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private ImportOrchestrator BuildOrchestrator(params IPropertyImporter[] importers)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var bulkWriter = new PropertyBulkWriter(fixture.DataSource);

        return new ImportOrchestrator(importers, NullLogger<ImportOrchestrator>.Instance, scopeFactory, bulkWriter);
    }

    private static Property MakeProperty(string source, string str = "Teststraße") =>
        new() { Str = str, Hnr = "1", Plz = "00000", Ort = "Testort", Gemeinde = "Testgemeinde", FlaecheAmtl = 100, Source = source };

    [Fact]
    public async Task CheckAndImportAsync_TwoSources_BothCoexist()
    {
        var importerA = new StubPropertyImporter("a", "ds", "file-a", "2026-01-01", [MakeProperty("a")]);
        var importerB = new StubPropertyImporter("b", "ds", "file-b", "2026-01-01", [MakeProperty("b")]);

        await BuildOrchestrator(importerA, importerB).CheckAndImportAsync(TestContext.Current.CancellationToken);

        await using var context = fixture.CreateContext();
        (await context.Properties.CountAsync(p => p.Source == "a", TestContext.Current.CancellationToken)).Should().Be(1);
        (await context.Properties.CountAsync(p => p.Source == "b", TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task CheckAndImportAsync_ReimportingOneSource_DoesNotDeleteOtherSourceRows()
    {
        var importerA = new StubPropertyImporter("a", "ds", "file-a", "2026-01-01", [MakeProperty("a")]);
        var importerB = new StubPropertyImporter("b", "ds", "file-b", "2026-01-01", [MakeProperty("b")]);
        await BuildOrchestrator(importerA, importerB).CheckAndImportAsync(TestContext.Current.CancellationToken);

        // A newer version of source A's data becomes available
        var importerANewer = new StubPropertyImporter("a", "ds", "file-a", "2026-02-01", [MakeProperty("a", "Neue Straße")]);
        await BuildOrchestrator(importerANewer, importerB).CheckAndImportAsync(TestContext.Current.CancellationToken);

        await using var context = fixture.CreateContext();
        (await context.Properties.CountAsync(p => p.Source == "b", TestContext.Current.CancellationToken)).Should().Be(1, "source B's rows must survive source A's re-import");
        var aRows = await context.Properties.Where(p => p.Source == "a").ToListAsync(TestContext.Current.CancellationToken);
        aRows.Should().ContainSingle().Which.Str.Should().Be("Neue Straße");
    }

    [Fact]
    public async Task CheckAndImportAsync_NothingNewer_RecordsTheCheckWithoutReimporting()
    {
        var importer = new StubPropertyImporter("a", "ds", "file-a", "2026-01-01", [MakeProperty("a")]);
        await BuildOrchestrator(importer).CheckAndImportAsync(TestContext.Current.CancellationToken);

        var beforeCheck = DateTimeOffset.UtcNow;
        await BuildOrchestrator(importer).CheckAndImportAsync(TestContext.Current.CancellationToken);

        await using var after = fixture.CreateContext();
        (await after.ImportRuns.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1, "the unchanged version isn't imported again");
        var state = await after.SourceStates.SingleAsync(TestContext.Current.CancellationToken);
        state.LastCheckedAt.Should().BeOnOrAfter(beforeCheck);
    }

    [Fact]
    public async Task CheckAndImportAsync_SameDatasetNamingAcrossSources_DedupIsScopedBySource()
    {
        // Two unrelated sources coincidentally reuse identical dataset/file/timestamp naming.
        var importerA = new StubPropertyImporter("a", "ds", "latest.zip", "2026-01-01", [MakeProperty("a")]);
        await BuildOrchestrator(importerA).CheckAndImportAsync(TestContext.Current.CancellationToken);

        var importerB = new StubPropertyImporter("b", "ds", "latest.zip", "2026-01-01", [MakeProperty("b")]);
        await BuildOrchestrator(importerB).CheckAndImportAsync(TestContext.Current.CancellationToken);

        await using var context = fixture.CreateContext();
        (await context.Properties.CountAsync(p => p.Source == "b", TestContext.Current.CancellationToken)).Should().Be(1, "dedup must be scoped by Source, not just dataset/file/timestamp");
    }

    [Fact]
    public async Task CheckAndImportAsync_OneImporterThrows_OthersStillRun()
    {
        var failing = new ThrowingPropertyImporter("broken");
        var working = new StubPropertyImporter("ok", "ds", "file", "2026-01-01", [MakeProperty("ok")]);

        var act = () => BuildOrchestrator(failing, working).CheckAndImportAsync();
        await act.Should().NotThrowAsync();

        await using var context = fixture.CreateContext();
        (await context.Properties.CountAsync(p => p.Source == "ok", TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task CheckAndImportAsync_ImporterTimesOut_IsTreatedAsThatSourcesFailure()
    {
        // HttpClient reports a timeout as TaskCanceledException, an OperationCanceledException.
        // Unless the run itself was cancelled, it must not escape and stop the host.
        var timingOut = new ThrowingPropertyImporter("slow", new TaskCanceledException("HttpClient timeout"));
        var working = new StubPropertyImporter("ok", "ds", "file", "2026-01-01", [MakeProperty("ok")]);

        var act = () => BuildOrchestrator(timingOut, working).CheckAndImportAsync(TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        await using var context = fixture.CreateContext();
        (await context.Properties.CountAsync(p => p.Source == "ok", TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task CheckAndImportAsync_RunCancelled_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var importer = new StubPropertyImporter("a", "ds", "file", "2026-01-01", [MakeProperty("a")]);

        var act = () => BuildOrchestrator(importer).CheckAndImportAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CheckAndImportAsync_FetchFailsMidway_KeepsPreviousRowsAndRetriesNextRun()
    {
        await BuildOrchestrator(new StubPropertyImporter("a", "ds", "file", "v1", [MakeProperty("a", "Alt")]))
            .CheckAndImportAsync(TestContext.Current.CancellationToken);

        var failing = new StubPropertyImporter("a", "ds", "file", "v2",
            [MakeProperty("a", "Neu 1"), MakeProperty("a", "Neu 2")], failAfter: 1);
        await BuildOrchestrator(failing).CheckAndImportAsync(TestContext.Current.CancellationToken);

        await using (var context = fixture.CreateContext())
        {
            (await context.Properties.Select(p => p.Str).ToListAsync(TestContext.Current.CancellationToken))
                .Should().Equal(["Alt"], "a failed import must not replace the previous data");
            var failed = await context.ImportRuns.SingleAsync(r => r.Fingerprint == "ds/file/v2", TestContext.Current.CancellationToken);
            failed.CompletedAt.Should().BeNull();
            failed.Error.Should().Contain("upstream failed midway");
        }

        var fixedImporter = new StubPropertyImporter("a", "ds", "file", "v2",
            [MakeProperty("a", "Neu 1"), MakeProperty("a", "Neu 2")]);
        await BuildOrchestrator(fixedImporter).CheckAndImportAsync(TestContext.Current.CancellationToken);

        await using (var context = fixture.CreateContext())
        {
            (await context.Properties.Select(p => p.Str).ToListAsync(TestContext.Current.CancellationToken))
                .Should().BeEquivalentTo(["Neu 1", "Neu 2"], "the failed version is retried, not skipped as already imported");
            var retried = await context.ImportRuns.SingleAsync(r => r.Fingerprint == "ds/file/v2" && r.FailedAt == null, TestContext.Current.CancellationToken);
            retried.CompletedAt.Should().NotBeNull();
        }
    }

    private sealed class StubPropertyImporter(
        string source, string datasetName, string fileName, string versionTimestamp, List<Property> properties,
        int? failAfter = null) : IPropertyImporter
    {
        public string Source => source;

        public Task<IReadOnlyList<ImportCandidate>> DiscoverAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ImportCandidate>>([new ImportCandidate(datasetName, fileName, versionTimestamp)]);

        public async IAsyncEnumerable<Property> FetchAsync(ImportCandidate candidate, [EnumeratorCancellation] CancellationToken ct)
        {
            var yielded = 0;
            foreach (var p in properties)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                if (yielded++ == failAfter)
                    throw new HttpRequestException("upstream failed midway");
                yield return p;
            }
        }
    }

    private sealed class ThrowingPropertyImporter(string source, Exception? exception = null) : IPropertyImporter
    {
        public string Source => source;

        public Task<IReadOnlyList<ImportCandidate>> DiscoverAsync(CancellationToken ct) =>
            throw exception ?? new InvalidOperationException("discovery boom");

        public IAsyncEnumerable<Property> FetchAsync(ImportCandidate candidate, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}

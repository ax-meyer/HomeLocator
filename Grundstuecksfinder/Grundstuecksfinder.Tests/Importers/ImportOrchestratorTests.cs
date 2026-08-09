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
public class ImportOrchestratorTests(PostgresFixture fixture) : IAsyncLifetime
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

        await BuildOrchestrator(importerA, importerB).CheckAndImportAsync();

        await using var context = fixture.CreateContext();
        (await context.Properties.CountAsync(p => p.Source == "a")).Should().Be(1);
        (await context.Properties.CountAsync(p => p.Source == "b")).Should().Be(1);
    }

    [Fact]
    public async Task CheckAndImportAsync_ReimportingOneSource_DoesNotDeleteOtherSourceRows()
    {
        var importerA = new StubPropertyImporter("a", "ds", "file-a", "2026-01-01", [MakeProperty("a")]);
        var importerB = new StubPropertyImporter("b", "ds", "file-b", "2026-01-01", [MakeProperty("b")]);
        await BuildOrchestrator(importerA, importerB).CheckAndImportAsync();

        // A newer version of source A's data becomes available
        var importerANewer = new StubPropertyImporter("a", "ds", "file-a", "2026-02-01", [MakeProperty("a", "Neue Straße")]);
        await BuildOrchestrator(importerANewer, importerB).CheckAndImportAsync();

        await using var context = fixture.CreateContext();
        (await context.Properties.CountAsync(p => p.Source == "b")).Should().Be(1, "source B's rows must survive source A's re-import");
        var aRows = await context.Properties.Where(p => p.Source == "a").ToListAsync();
        aRows.Should().ContainSingle().Which.Str.Should().Be("Neue Straße");
    }

    [Fact]
    public async Task CheckAndImportAsync_SameDatasetNamingAcrossSources_DedupIsScopedBySource()
    {
        // Two unrelated sources coincidentally reuse identical dataset/file/timestamp naming.
        var importerA = new StubPropertyImporter("a", "ds", "latest.zip", "2026-01-01", [MakeProperty("a")]);
        await BuildOrchestrator(importerA).CheckAndImportAsync();

        var importerB = new StubPropertyImporter("b", "ds", "latest.zip", "2026-01-01", [MakeProperty("b")]);
        await BuildOrchestrator(importerB).CheckAndImportAsync();

        await using var context = fixture.CreateContext();
        (await context.Properties.CountAsync(p => p.Source == "b")).Should().Be(1, "dedup must be scoped by Source, not just dataset/file/timestamp");
    }

    [Fact]
    public async Task CheckAndImportAsync_OneImporterThrows_OthersStillRun()
    {
        var failing = new ThrowingPropertyImporter("broken");
        var working = new StubPropertyImporter("ok", "ds", "file", "2026-01-01", [MakeProperty("ok")]);

        var act = () => BuildOrchestrator(failing, working).CheckAndImportAsync();
        await act.Should().NotThrowAsync();

        await using var context = fixture.CreateContext();
        (await context.Properties.CountAsync(p => p.Source == "ok")).Should().Be(1);
    }

    private sealed class StubPropertyImporter(
        string source, string datasetName, string fileName, string versionTimestamp, List<Property> properties) : IPropertyImporter
    {
        public string Source => source;

        public Task<IReadOnlyList<ImportCandidate>> DiscoverAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ImportCandidate>>([new ImportCandidate(datasetName, fileName, versionTimestamp)]);

        public async IAsyncEnumerable<Property> FetchAsync(ImportCandidate candidate, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var p in properties)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return p;
            }
        }
    }

    private sealed class ThrowingPropertyImporter(string source) : IPropertyImporter
    {
        public string Source => source;

        public Task<IReadOnlyList<ImportCandidate>> DiscoverAsync(CancellationToken ct) =>
            throw new InvalidOperationException("discovery boom");

        public IAsyncEnumerable<Property> FetchAsync(ImportCandidate candidate, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}

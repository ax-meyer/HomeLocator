using FluentAssertions;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services;
using Grundstuecksfinder.Tests.TestHelpers;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Grundstuecksfinder.Tests;

[Collection("Postgres")]
public sealed class FilterOptionsServiceIntegrationTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Dispose() => _cache.Dispose();

    private async Task<FilterOptions> GetOptionsAsync()
    {
        await using var context = fixture.CreateContext();
        var service = new FilterOptionsService(new PropertyService(context, DisabledSources.None), context, _cache);
        return await service.GetAsync();
    }

    private static SearchLocation Plz(string plz) => new(plz, IsPlz: true);
    private static SearchLocation Gemeinde(string gemeinde) => new(gemeinde, IsPlz: false);

    /// <summary>What an import leaves behind: its rows, and its run as the source's served one.</summary>
    private async Task AddImportAsync(double day, params (string Plz, string Gemeinde)[] rows)
    {
        await using var context = fixture.CreateContext();
        var run = ImportSeed.Completed("nrw", ImportSeed.Day(day), rows.Length);
        context.Properties.AddRange(rows.Select(r =>
            new Property { Str = "Hauptstraße", Hnr = "1", Plz = r.Plz, Gemeinde = r.Gemeinde, Source = "nrw", ImportRun = run }));

        var state = await context.SourceStates.FindAsync(["nrw"], TestContext.Current.CancellationToken);
        if (state is null)
            context.SourceStates.Add(ImportSeed.Serving(run));
        else
            state.ServedRun = run;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetAsync_UnchangedData_IsServedFromCache()
    {
        await AddImportAsync(1, ("50667", "Köln"));
        await GetOptionsAsync();

        // A row appearing without a completed import can't happen in the app; here it shows
        // that the second call didn't query Properties again.
        await using (var context = fixture.CreateContext())
        {
            var run = context.ImportRuns.Single();
            context.Properties.Add(new Property { Str = "Zeil", Hnr = "1", Plz = "60313", Gemeinde = "Frankfurt am Main", Source = "nrw", ImportRunId = run.Id });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var options = await GetOptionsAsync();

        options.Locations.Should().Equal(Plz("50667"), Gemeinde("Köln"));
    }

    [Fact]
    public async Task GetAsync_AfterANewerImportCompletes_RebuildsTheLists()
    {
        await AddImportAsync(1, ("50667", "Köln"));
        await GetOptionsAsync();

        await AddImportAsync(2, ("24103", "Kiel"));
        var options = await GetOptionsAsync();

        options.Locations.Should().Equal(Plz("24103"), Plz("50667"), Gemeinde("Kiel"), Gemeinde("Köln"));
    }

    [Fact]
    public async Task GetAsync_NoImportYet_ReturnsEmptyLists()
    {
        var options = await GetOptionsAsync();

        options.Locations.Should().BeEmpty();
    }
}

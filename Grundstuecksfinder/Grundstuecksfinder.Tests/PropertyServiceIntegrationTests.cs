using FluentAssertions;
using Grundstuecksfinder.Models;
using Xunit;
using Grundstuecksfinder.Services;
using Grundstuecksfinder.Tests.TestHelpers;
using static Grundstuecksfinder.Tests.TestHelpers.ImportSeed;

namespace Grundstuecksfinder.Tests;

[Collection("Postgres")]
public sealed class PropertyServiceIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Property.ImportRunId is a required FK, so every seeded property needs a parent run.
    /// Assigning the navigation property lets EF insert both in the right order.
    /// </summary>
    private static ImportRun NewRun() => Completed("nrw", Day(0), recordCount: 0);

    [Fact]
    public async Task GetPropertiesAsync_FilterByPlz_ReturnsMatchingRows()
    {
        await using var context = fixture.CreateContext();
        var run = NewRun();
        context.Properties.AddRange(
            new Property { Str = "Hauptstraße", Hnr = "1", Plz = "50667", Ort = "Köln", Gemeinde = "Köln", FlaecheAmtl = 200, ImportRun = run },
            new Property { Str = "Bergstraße", Hnr = "5", Plz = "44139", Ort = "Dortmund", Gemeinde = "Dortmund", FlaecheAmtl = 500, ImportRun = run }
        );
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var queryContext = fixture.CreateContext();
        var service = new PropertyService(queryContext, DisabledSources.None);

        var result = await service.GetPropertiesAsync(plz: "50667", cancellationToken: TestContext.Current.CancellationToken);

        result.Should().ContainSingle()
            .Which.Plz.Should().Be("50667");
    }

    [Fact]
    public async Task GetPropertiesAsync_FilterBySizeRange_ReturnsCorrectSubset()
    {
        await using var context = fixture.CreateContext();
        var run = NewRun();
        context.Properties.AddRange(
            new Property { Plz = "44139", Ort = "Dortmund", Gemeinde = "Dortmund", FlaecheAmtl = 100, ImportRun = run },
            new Property { Plz = "44139", Ort = "Dortmund", Gemeinde = "Dortmund", FlaecheAmtl = 500, ImportRun = run },
            new Property { Plz = "44139", Ort = "Dortmund", Gemeinde = "Dortmund", FlaecheAmtl = 1000, ImportRun = run }
        );
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var queryContext = fixture.CreateContext();
        var service = new PropertyService(queryContext, DisabledSources.None);

        var result = await service.GetPropertiesAsync(minFlaeche: 400, maxFlaeche: 600, cancellationToken: TestContext.Current.CancellationToken);

        result.Should().ContainSingle()
            .Which.FlaecheAmtl.Should().Be(500);
    }

    [Fact]
    public async Task GetLastCheckedAtAsync_NothingServed_ReturnsNull()
    {
        await using var context = fixture.CreateContext();
        var service = new PropertyService(context, DisabledSources.None);

        var result = await service.GetLastCheckedAtAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLastCheckedAtAsync_UsesEachSourcesLatestCheckAndTheOldestAcrossSources()
    {
        await using var context = fixture.CreateContext();
        context.ImportRuns.Add(Failed("nrw", Day(9.5), "boom"));
        context.SourceStates.AddRange(
            // nrw: imported long ago, confirmed current by a recent check; the failed retry doesn't count.
            Serving(Completed("nrw", Day(1), recordCount: 10), lastCheckedAt: Day(9)),
            // sh: last confirmed earlier, so it holds the date back.
            Serving(Completed("sh", Day(6), recordCount: 5), lastCheckedAt: Day(7)),
            // hh: no data yet, so nothing to confirm.
            new SourceState { Source = "hh", LastProbeAt = Day(8) });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var queryContext = fixture.CreateContext();
        var service = new PropertyService(queryContext, DisabledSources.None);

        (await service.GetLastCheckedAtAsync()).Should().Be(Day(7));
    }

    [Fact]
    public async Task GetLastCheckedAtAsync_NeverChecked_FallsBackToCompletion()
    {
        await using var context = fixture.CreateContext();
        var state = Serving(Completed("nrw", Day(4), recordCount: 10));
        state.LastCheckedAt = null;
        context.SourceStates.Add(state);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var queryContext = fixture.CreateContext();
        var service = new PropertyService(queryContext, DisabledSources.None);

        (await service.GetLastCheckedAtAsync()).Should().Be(Day(4));
    }

    [Fact]
    public async Task DisabledSource_IsHiddenFromSearchFiltersAndCounts()
    {
        await using var context = fixture.CreateContext();
        var nrwRun = Completed("nrw", Day(1), recordCount: 1);
        var heRun = Completed("he", Day(2), recordCount: 1);
        context.SourceStates.AddRange(Serving(nrwRun), Serving(heRun));
        context.Properties.AddRange(
            new Property { Str = "Hauptstraße", Hnr = "1", Plz = "50667", Gemeinde = "Köln", FlaecheAmtl = 500, Source = "nrw", ImportRun = nrwRun },
            new Property { Str = "Zeil", Hnr = "1", Plz = "60313", Gemeinde = "Frankfurt am Main", FlaecheAmtl = 500, Source = "he", ImportRun = heRun });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var queryContext = fixture.CreateContext();
        var service = new PropertyService(queryContext, new DisabledSources(["he"]));

        (await service.GetPropertiesAsync(minFlaeche: 100, cancellationToken: TestContext.Current.CancellationToken)).Should().ContainSingle().Which.Source.Should().Be("nrw");
        (await service.GetDistinctGemeindenAsync()).Should().Equal("Köln");
        (await service.GetDistinctPlzAsync()).Should().Equal("50667");
        (await service.GetTotalPropertyCountAsync()).Should().Be(1);
        (await service.GetLastCheckedAtAsync()).Should().Be(Day(1), "only the visible nrw import counts");
    }

    [Fact]
    public async Task DisabledSource_WithOnlyItsImportServed_HasNoLastCheck()
    {
        await using var context = fixture.CreateContext();
        context.SourceStates.AddRange(
            Serving(Completed("nrw", Day(1), recordCount: 0)),
            Serving(Completed("he", Day(2), recordCount: 5)));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var queryContext = fixture.CreateContext();
        var service = new PropertyService(queryContext, new DisabledSources(["he"]));

        (await service.GetLastCheckedAtAsync()).Should().BeNull("the only non-empty import belongs to the disabled source");
        (await service.GetTotalPropertyCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task GetTotalPropertyCountAsync_SumsEachSourcesServedRun()
    {
        await using var context = fixture.CreateContext();
        context.ImportRuns.AddRange(
            // Superseded by the newer nrw import, which replaced its rows.
            Completed("nrw", Day(1), recordCount: 100),
            // Failed attempt: its rows were never swapped in.
            Failed("nrw", Day(4), "boom"));
        context.SourceStates.AddRange(
            Serving(Completed("nrw", Day(3), recordCount: 120)),
            Serving(Completed("sh", Day(2), recordCount: 30)));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var queryContext = fixture.CreateContext();
        var service = new PropertyService(queryContext, DisabledSources.None);

        (await service.GetTotalPropertyCountAsync()).Should().Be(150);
    }
}

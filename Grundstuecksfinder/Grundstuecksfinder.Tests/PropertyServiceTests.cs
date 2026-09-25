using FluentAssertions;
using Grundstuecksfinder.Data;
using Xunit;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services;
using Grundstuecksfinder.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;

namespace Grundstuecksfinder.Tests;

public sealed class PropertyServiceTests : IAsyncLifetime
{
    private AppDbContext _context = null!;
    private PropertyService _service = null!;

    public async ValueTask InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new AppDbContext(options);
        await _context.Database.EnsureCreatedAsync();
        _service = new PropertyService(_context, DisabledSources.None);

        _context.Properties.AddRange(
            new Property { Str = "Hauptstraße", Hnr = "1", Plz = "50667", Ort = "Köln", Gemeinde = "Köln", FlaecheAmtl = 300 },
            new Property { Str = "Nebenstraße", Hnr = "2", Plz = "50667", Ort = "Köln", Gemeinde = "Köln", FlaecheAmtl = 600 },
            new Property { Str = "Bergstraße", Hnr = "10", Plz = "44139", Ort = "Dortmund", Gemeinde = "Dortmund", FlaecheAmtl = 450 },
            new Property { Str = "Waldweg", Hnr = "5", Plz = "44139", Ort = "Dortmund", Gemeinde = "Dortmund", FlaecheAmtl = 1200 }
        );
        await _context.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task GetPropertiesAsync_FilterByPlz_ReturnsOnlyMatchingEntries()
    {
        var result = await _service.GetPropertiesAsync(plz: "50667", cancellationToken: TestContext.Current.CancellationToken);

        result.Should().HaveCount(2)
            .And.AllSatisfy(p => p.Plz.Should().Be("50667"));
    }

    [Fact]
    public async Task GetPropertiesAsync_FilterByGemeinde_ReturnsOnlyMatchingEntries()
    {
        var result = await _service.GetPropertiesAsync(gemeinde: "Dortmund", cancellationToken: TestContext.Current.CancellationToken);

        result.Should().HaveCount(2)
            .And.AllSatisfy(p => p.Gemeinde.Should().Be("Dortmund"));
    }

    [Fact]
    public async Task GetPropertiesAsync_FilterByMinFlaeche_ExcludesSmallerEntries()
    {
        var result = await _service.GetPropertiesAsync(plz: "50667", minFlaeche: 500, cancellationToken: TestContext.Current.CancellationToken);

        result.Should().ContainSingle()
            .Which.FlaecheAmtl.Should().Be(600);
    }

    [Fact]
    public async Task GetPropertiesAsync_FilterByMaxFlaeche_ExcludesLargerEntries()
    {
        var result = await _service.GetPropertiesAsync(gemeinde: "Dortmund", maxFlaeche: 500, cancellationToken: TestContext.Current.CancellationToken);

        result.Should().ContainSingle()
            .Which.FlaecheAmtl.Should().Be(450);
    }

    [Fact]
    public async Task GetPropertiesAsync_CombinedFilters_ApplyAll()
    {
        var result = await _service.GetPropertiesAsync(
            gemeinde: "Dortmund", minFlaeche: 1000, maxFlaeche: 2000,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().ContainSingle()
            .Which.FlaecheAmtl.Should().Be(1200);
    }

    [Fact]
    public async Task GetPropertiesAsync_NoMatch_ReturnsEmpty()
    {
        var result = await _service.GetPropertiesAsync(plz: "00000", cancellationToken: TestContext.Current.CancellationToken);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPropertiesAsync_LimitIsRespected()
    {
        var result = await _service.GetPropertiesAsync(limit: 2, cancellationToken: TestContext.Current.CancellationToken);

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetRetrievalYearsAsync_ReturnsTheYearOfEachServedRun()
    {
        // Mid-year, so the server's time zone can't move either date into another year.
        var niRun = ImportSeed.Completed("ni", new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero));
        var bwRun = ImportSeed.Completed("bw", new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero));
        _context.SourceStates.AddRange(
            ImportSeed.Serving(niRun),
            ImportSeed.Serving(bwRun),
            new SourceState { Source = "sl", LastProbeAt = ImportSeed.Day(1) });
        // A newer run that failed doesn't count: the rows shown are still from 2025.
        _context.ImportRuns.Add(ImportSeed.Failed("ni", new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero), "boom"));
        await _context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var years = await _service.GetRetrievalYearsAsync();

        years.Should().BeEquivalentTo(new Dictionary<string, int> { ["ni"] = 2025, ["bw"] = 2026 });
    }
}

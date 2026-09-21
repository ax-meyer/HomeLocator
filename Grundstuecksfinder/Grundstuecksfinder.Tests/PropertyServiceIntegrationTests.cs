using FluentAssertions;
using Grundstuecksfinder.Models;
using Xunit;
using Grundstuecksfinder.Services;
using Grundstuecksfinder.Tests.TestHelpers;

namespace Grundstuecksfinder.Tests;

[Collection("Postgres")]
public sealed class PropertyServiceIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Property.ImportLogId is a required FK, so every seeded property needs a parent
    /// import log. Assigning the navigation property lets EF insert both in the right order.
    /// </summary>
    private static ImportLog NewImportLog() => new()
    {
        DatasetName = "test",
        FileName = "test.zip",
        FileTimestamp = "2026-01-01T00:00:00",
        ImportedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        RecordCount = 0,
    };

    [Fact]
    public async Task GetPropertiesAsync_FilterByPlz_ReturnsMatchingRows()
    {
        await using var context = fixture.CreateContext();
        var importLog = NewImportLog();
        context.Properties.AddRange(
            new Property { Str = "Hauptstraße", Hnr = "1", Plz = "50667", Ort = "Köln", Gemeinde = "Köln", FlaecheAmtl = 200, ImportLog = importLog },
            new Property { Str = "Bergstraße", Hnr = "5", Plz = "44139", Ort = "Dortmund", Gemeinde = "Dortmund", FlaecheAmtl = 500, ImportLog = importLog }
        );
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var queryContext = fixture.CreateContext();
        var service = new PropertyService(queryContext);

        var result = await service.GetPropertiesAsync(plz: "50667");

        result.Should().ContainSingle()
            .Which.Plz.Should().Be("50667");
    }

    [Fact]
    public async Task GetPropertiesAsync_FilterBySizeRange_ReturnsCorrectSubset()
    {
        await using var context = fixture.CreateContext();
        var importLog = NewImportLog();
        context.Properties.AddRange(
            new Property { Plz = "44139", Ort = "Dortmund", Gemeinde = "Dortmund", FlaecheAmtl = 100, ImportLog = importLog },
            new Property { Plz = "44139", Ort = "Dortmund", Gemeinde = "Dortmund", FlaecheAmtl = 500, ImportLog = importLog },
            new Property { Plz = "44139", Ort = "Dortmund", Gemeinde = "Dortmund", FlaecheAmtl = 1000, ImportLog = importLog }
        );
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var queryContext = fixture.CreateContext();
        var service = new PropertyService(queryContext);

        var result = await service.GetPropertiesAsync(minFlaeche: 400, maxFlaeche: 600);

        result.Should().ContainSingle()
            .Which.FlaecheAmtl.Should().Be(500);
    }

    [Fact]
    public async Task GetLastImportAsync_NoLogs_ReturnsNull()
    {
        await using var context = fixture.CreateContext();
        var service = new PropertyService(context);

        var result = await service.GetLastImportAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLastImportAsync_MultipleImports_ReturnsMostRecent()
    {
        var older = DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds();
        var newer = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();

        await using var context = fixture.CreateContext();
        context.ImportLogs.AddRange(
            new ImportLog { DatasetName = "ds1", FileName = "a.zip", FileTimestamp = "ts1", ImportedAt = older, RecordCount = 10 },
            new ImportLog { DatasetName = "ds1", FileName = "b.zip", FileTimestamp = "ts2", ImportedAt = newer, RecordCount = 20 }
        );
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var queryContext = fixture.CreateContext();
        var service = new PropertyService(queryContext);

        var result = await service.GetLastImportAsync();

        result.Should().NotBeNull();
        result!.RecordCount.Should().Be(20);
    }
}

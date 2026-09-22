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
        var service = new PropertyService(queryContext, DisabledSources.None);

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
        var service = new PropertyService(queryContext, DisabledSources.None);

        var result = await service.GetPropertiesAsync(minFlaeche: 400, maxFlaeche: 600);

        result.Should().ContainSingle()
            .Which.FlaecheAmtl.Should().Be(500);
    }

    [Fact]
    public async Task GetLastImportAsync_NoLogs_ReturnsNull()
    {
        await using var context = fixture.CreateContext();
        var service = new PropertyService(context, DisabledSources.None);

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
            new ImportLog { DatasetName = "ds1", FileName = "a.zip", FileTimestamp = "ts1", ImportedAt = older, RecordCount = 10, CompletedAt = older },
            new ImportLog { DatasetName = "ds1", FileName = "b.zip", FileTimestamp = "ts2", ImportedAt = newer, RecordCount = 20, CompletedAt = newer },
            // Newest, but still running (or failed): not the "last import" shown to users.
            new ImportLog { DatasetName = "ds1", FileName = "c.zip", FileTimestamp = "ts3", ImportedAt = newer + 1, RecordCount = 0 }
        );
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var queryContext = fixture.CreateContext();
        var service = new PropertyService(queryContext, DisabledSources.None);

        var result = await service.GetLastImportAsync();

        result.Should().NotBeNull();
        result!.RecordCount.Should().Be(20);
    }

    [Fact]
    public async Task DisabledSource_IsHiddenFromSearchFiltersAndCounts()
    {
        await using var context = fixture.CreateContext();
        var nrwLog = NewImportLog();
        nrwLog.Source = "nrw";
        nrwLog.RecordCount = 1;
        nrwLog.CompletedAt = 1;
        var heLog = NewImportLog();
        heLog.Source = "he";
        heLog.FileName = "he.zip";
        heLog.RecordCount = 1;
        heLog.CompletedAt = 1;
        context.Properties.AddRange(
            new Property { Str = "Hauptstraße", Hnr = "1", Plz = "50667", Gemeinde = "Köln", FlaecheAmtl = 500, Source = "nrw", ImportLog = nrwLog },
            new Property { Str = "Zeil", Hnr = "1", Plz = "60313", Gemeinde = "Frankfurt am Main", FlaecheAmtl = 500, Source = "he", ImportLog = heLog });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var queryContext = fixture.CreateContext();
        var service = new PropertyService(queryContext, new DisabledSources(["he"]));

        (await service.GetPropertiesAsync(minFlaeche: 100)).Should().ContainSingle().Which.Source.Should().Be("nrw");
        (await service.GetDistinctGemeindenAsync()).Should().Equal("Köln");
        (await service.GetDistinctPlzAsync()).Should().Equal("50667");
        (await service.GetTotalPropertyCountAsync()).Should().Be(1);
        (await service.GetLastImportAsync())!.Source.Should().Be("nrw");
    }

    [Fact]
    public async Task DisabledSource_WithOnlyItsImportCompleted_HasNoLastImport()
    {
        await using var context = fixture.CreateContext();
        context.ImportLogs.AddRange(
            new ImportLog { Source = "nrw", DatasetName = "ds", FileName = "nrw.zip", FileTimestamp = "ts", ImportedAt = 1, RecordCount = 0 },
            new ImportLog { Source = "he", DatasetName = "ds", FileName = "he.zip", FileTimestamp = "ts", ImportedAt = 2, RecordCount = 5, CompletedAt = 2 });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var queryContext = fixture.CreateContext();
        var service = new PropertyService(queryContext, new DisabledSources(["he"]));

        (await service.GetLastImportAsync()).Should().BeNull("the only non-empty import belongs to the disabled source");
        (await service.GetTotalPropertyCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task GetTotalPropertyCountAsync_SumsLatestCompletedImportPerSource()
    {
        await using var context = fixture.CreateContext();
        context.ImportLogs.AddRange(
            // Superseded by the newer nrw import, which replaced its rows.
            new ImportLog { Source = "nrw", DatasetName = "ds", FileName = "a.zip", FileTimestamp = "ts1", ImportedAt = 1, RecordCount = 100, CompletedAt = 1 },
            new ImportLog { Source = "nrw", DatasetName = "ds", FileName = "b.zip", FileTimestamp = "ts2", ImportedAt = 3, RecordCount = 120, CompletedAt = 3 },
            // Failed attempt: its rows were never swapped in.
            new ImportLog { Source = "nrw", DatasetName = "ds", FileName = "c.zip", FileTimestamp = "ts3", ImportedAt = 4, RecordCount = 0 },
            new ImportLog { Source = "sh", DatasetName = "ds", FileName = "sh", FileTimestamp = "ts1", ImportedAt = 2, RecordCount = 30, CompletedAt = 2 });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var queryContext = fixture.CreateContext();
        var service = new PropertyService(queryContext, DisabledSources.None);

        (await service.GetTotalPropertyCountAsync()).Should().Be(150);
    }
}

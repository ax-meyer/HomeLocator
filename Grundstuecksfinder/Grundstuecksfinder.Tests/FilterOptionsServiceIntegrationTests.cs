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

    private async Task AddImportAsync(long completedAt, params (string Plz, string Gemeinde)[] rows)
    {
        await using var context = fixture.CreateContext();
        var log = new ImportLog
        {
            Source = "nrw", DatasetName = "ds", FileName = $"{completedAt}.zip", FileTimestamp = $"{completedAt}",
            ImportedAt = completedAt, RecordCount = rows.Length, CompletedAt = completedAt,
        };
        context.Properties.AddRange(rows.Select(r =>
            new Property { Str = "Hauptstraße", Hnr = "1", Plz = r.Plz, Gemeinde = r.Gemeinde, Source = "nrw", ImportLog = log }));
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
            var log = context.ImportLogs.Single();
            context.Properties.Add(new Property { Str = "Zeil", Hnr = "1", Plz = "60313", Gemeinde = "Frankfurt am Main", Source = "nrw", ImportLogId = log.Id });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var options = await GetOptionsAsync();

        options.Plz.Should().Equal("50667");
        options.Gemeinden.Should().Equal("Köln");
    }

    [Fact]
    public async Task GetAsync_AfterANewerImportCompletes_RebuildsTheLists()
    {
        await AddImportAsync(1, ("50667", "Köln"));
        await GetOptionsAsync();

        await AddImportAsync(2, ("24103", "Kiel"));
        var options = await GetOptionsAsync();

        options.Plz.Should().Equal("24103", "50667");
        options.Gemeinden.Should().Equal("Kiel", "Köln");
    }

    [Fact]
    public async Task GetAsync_NoImportYet_ReturnsEmptyLists()
    {
        var options = await GetOptionsAsync();

        options.Plz.Should().BeEmpty();
        options.Gemeinden.Should().BeEmpty();
    }
}

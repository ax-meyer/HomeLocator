using FluentAssertions;
using Grundstuecksfinder.Data;
using Grundstuecksfinder.Infrastructure;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services;
using Grundstuecksfinder.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Grundstuecksfinder.Tests;

[Collection("Postgres")]
public sealed class ImportHealthCheckTests(PostgresFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task SeedAsync(params ImportLog[] logs)
    {
        await using var context = fixture.CreateContext();
        context.ImportLogs.AddRange(logs);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static ImportLog Log(string source, string version, long importedAt, string? error = null, int skippedTiles = 0) => new()
    {
        Source = source, DatasetName = "ds", FileName = "f", FileTimestamp = version,
        ImportedAt = importedAt, LastError = error, CompletedAt = error is null ? importedAt : null,
        SkippedTiles = skippedTiles,
    };

    private async Task<HealthCheckResult> CheckAsync(DisabledSources? disabled = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return await new ImportHealthCheck(scopeFactory, disabled ?? DisabledSources.None)
            .CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LatestAttemptFailed_IsDegradedAndNamesTheSource()
    {
        await SeedAsync(Log("he", "v1", 1), Log("he", "v2", 2, "fetched only 90 of 100 addresses"), Log("sn", "v1", 1));

        var result = await CheckAsync();

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("he: fetched only 90 of 100 addresses").And.NotContain("sn");
    }

    [Fact]
    public async Task FailureFollowedByASuccess_IsHealthy()
    {
        await SeedAsync(Log("he", "v1", 1, "boom"), Log("he", "v2", 2));

        (await CheckAsync()).Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task LatestImportSkippedTiles_IsDegradedAlthoughItSucceeded()
    {
        // The import is complete enough to serve, but nothing retries those tiles before the
        // source's version changes, so the hole must not be invisible.
        await SeedAsync(Log("bw", "v1", 1, skippedTiles: 3), Log("sn", "v1", 1));

        var result = await CheckAsync();

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("bw: 3").And.NotContain("sn");
    }

    [Fact]
    public async Task SkippedTilesFollowedByACleanImport_IsHealthy()
    {
        await SeedAsync(Log("bw", "v1", 1, skippedTiles: 3), Log("bw", "v2", 2));

        (await CheckAsync()).Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task FailureAndSkippedTiles_AreBothReported()
    {
        await SeedAsync(Log("he", "v1", 1, "boom"), Log("bw", "v1", 1, skippedTiles: 2));

        var result = await CheckAsync();

        result.Description.Should().Contain("he: boom").And.Contain("bw: 2");
    }

    [Fact]
    public async Task FailureOfADisabledSource_IsIgnored()
    {
        await SeedAsync(Log("he", "v1", 1, "boom"));

        (await CheckAsync(new DisabledSources(["he"]))).Status.Should().Be(HealthStatus.Healthy);
    }
}

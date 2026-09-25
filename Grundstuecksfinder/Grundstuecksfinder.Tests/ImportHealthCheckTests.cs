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
using static Grundstuecksfinder.Tests.TestHelpers.ImportSeed;

namespace Grundstuecksfinder.Tests;

[Collection("Postgres")]
public sealed class ImportHealthCheckTests(PostgresFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Runs are inserted one by one, so their IDs follow the argument order.</summary>
    private async Task SeedAsync(IEnumerable<ImportRun> runs, params SourceState[] states)
    {
        await using var context = fixture.CreateContext();
        foreach (var run in runs)
        {
            context.ImportRuns.Add(run);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        context.SourceStates.AddRange(states);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<HealthCheckResult> CheckAsync(DisabledSources? disabled = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return await new ImportHealthCheck(scopeFactory, disabled ?? DisabledSources.None)
            .CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NoImportsYet_IsHealthy() =>
        (await CheckAsync()).Status.Should().Be(HealthStatus.Healthy);

    [Fact]
    public async Task LatestAttemptFailed_IsDegradedAndNamesTheSource()
    {
        var he = Completed("he", Day(1));
        var sn = Completed("sn", Day(1));
        await SeedAsync([he, sn, Failed("he", Day(2), "fetched only 90 of 100 addresses")], Serving(he), Serving(sn));

        var result = await CheckAsync();

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("he: fetched only 90 of 100 addresses").And.NotContain("sn");
    }

    [Fact]
    public async Task FailureFollowedByASuccess_IsHealthy()
    {
        var he = Completed("he", Day(2));
        await SeedAsync([Failed("he", Day(1), "boom"), he], Serving(he));

        (await CheckAsync()).Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task LatestAttempt_IsTheLastToFinish_NotTheLastToStart()
    {
        // Two overlapping runs: the one started first (lower ID) completed after the other failed.
        var completedLater = Completed("he", Day(3));
        completedLater.StartedAt = Day(1);
        await SeedAsync([completedLater, Failed("he", Day(2), "boom")], Serving(completedLater));

        (await CheckAsync()).Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task FailureFinishingAfterAnOverlappingSuccess_IsReported()
    {
        var he = Completed("he", Day(2));
        var failedLater = Failed("he", Day(3), "boom");
        failedLater.StartedAt = Day(1);
        await SeedAsync([failedLater, he], Serving(he));

        (await CheckAsync()).Description.Should().Contain("he: boom");
    }

    [Fact]
    public async Task FailureFollowedByAnUnfinishedRun_IsStillReported()
    {
        // A retry that is still going (or was cut short by a crash) hasn't shown anything yet.
        await SeedAsync([Failed("he", Day(1), "boom"), Unfinished("he", Day(2))]);

        (await CheckAsync()).Description.Should().Contain("he: boom");
    }

    [Fact]
    public async Task ServedRunSkippedParts_IsDegradedAlthoughItSucceeded()
    {
        // The import is complete enough to serve, but nothing fills the hole before the next
        // re-import, so it must not be invisible.
        var bw = Completed("bw", Day(1), skippedParts: 3);
        var sn = Completed("sn", Day(1));
        await SeedAsync([bw, sn], Serving(bw), Serving(sn));

        var result = await CheckAsync();

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("bw: 3").And.NotContain("sn");
    }

    [Fact]
    public async Task SkippedPartsReplacedByACleanImport_IsHealthy()
    {
        var clean = Completed("bw", Day(2));
        await SeedAsync([Completed("bw", Day(1), skippedParts: 3), clean], Serving(clean));

        (await CheckAsync()).Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task FailureAndSkippedParts_AreBothReported()
    {
        var bw = Completed("bw", Day(1), skippedParts: 2);
        await SeedAsync([Failed("he", Day(1), "boom"), bw], Serving(bw));

        var result = await CheckAsync();

        result.Description.Should().Contain("he: boom").And.Contain("bw: 2");
    }

    [Fact]
    public async Task ProbeFailing_WithoutServedData_IsDegraded()
    {
        await SeedAsync([], new SourceState { Source = "sh", LastProbeAt = Day(1), LastProbeError = "503 Service Unavailable" });

        var result = await CheckAsync();

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("sh: 503 Service Unavailable");
    }

    [Fact]
    public async Task ProbeFailing_WhileDataIsServed_IsHealthy()
    {
        // The served data is no worse for a failed probe; the source is simply skipped this run.
        var sh = Completed("sh", Day(1));
        var state = Serving(sh);
        state.LastProbeError = "503 Service Unavailable";
        await SeedAsync([sh], state);

        (await CheckAsync()).Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task ProblemsOfADisabledSource_AreIgnored()
    {
        var bw = Completed("bw", Day(1), skippedParts: 2);
        await SeedAsync([Failed("he", Day(1), "boom"), bw],
            Serving(bw), new SourceState { Source = "he", LastProbeError = "boom" });

        (await CheckAsync(new DisabledSources(["he", "bw"]))).Status.Should().Be(HealthStatus.Healthy);
    }
}

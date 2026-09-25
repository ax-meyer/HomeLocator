using FluentAssertions;
using Grundstuecksfinder.Data;
using Grundstuecksfinder.Infrastructure;
using Grundstuecksfinder.Services.Importers;
using Grundstuecksfinder.Services.Importers.Scheduling;
using Grundstuecksfinder.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Xunit;

namespace Grundstuecksfinder.Tests;

public sealed class ImportWorkerTests
{
    [Theory]
    [InlineData("2026-09-24T02:00:00Z", "01:00:00")]
    [InlineData("2026-09-24T03:00:00Z", "1.00:00:00")] // a run ending right at 03:00 waits a day, not zero
    [InlineData("2026-09-24T03:00:01Z", "23:59:59")]
    [InlineData("2026-09-24T23:30:00+02:00", "05:30:00")] // 21:30 UTC
    public void TimeUntilNext_IsTheNextNightlyRunInUtc(string now, string expected) =>
        ImportWorker.TimeUntilNext(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture), ImportWorker.NightlyRunAt)
            .Should().Be(TimeSpan.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public async Task FailingRun_IsLoggedAndTheNextOneStillRuns()
    {
        // Nothing listens on port 1, so every run fails at its first database access. That must
        // not end the worker: an exception escaping a BackgroundService stops the whole host.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 24, 2, 0, 0, TimeSpan.Zero));
        var logger = new CapturingLogger<ImportWorker>();
        await using var services = UnreachableDatabaseServices(time);
        using var worker = new ImportWorker(services.GetRequiredService<IServiceScopeFactory>(), time, logger);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await Eventually.WaitUntilAsync(() => FailedRuns(logger) == 1 && logger.MessagesAt(LogLevel.Information).Any());
        logger.MessagesAt(LogLevel.Information).Should().ContainSingle(m => m.Contains("01:00:00", StringComparison.Ordinal),
            "the startup run is followed by the nightly one at 03:00 UTC");

        // Keep nudging the clock: the worker may not have started its delay yet.
        await Eventually.WaitUntilAsync(() => FailedRuns(logger) >= 2, () => time.Advance(TimeSpan.FromHours(1)));

        worker.ExecuteTask!.IsFaulted.Should().BeFalse();
        await worker.StopAsync(TestContext.Current.CancellationToken);
        worker.ExecuteTask.IsCompleted.Should().BeTrue("stopping the host ends the worker");
        worker.ExecuteTask.IsFaulted.Should().BeFalse();
    }

    private static int FailedRuns(CapturingLogger<ImportWorker> logger) =>
        logger.MessagesAt(LogLevel.Error).Count(m => m.Contains("Import run failed", StringComparison.Ordinal));

    private static ServiceProvider UnreachableDatabaseServices(TimeProvider time)
    {
        const string connectionString = "Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=2";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(time);
        services.AddSingleton(NpgsqlDataSource.Create(connectionString));
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));
        services.AddSingleton(new RefreshOptions());
        services.AddScoped(sp => new PropertyBulkWriter(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddScoped<ImportStateStore>();
        services.AddScoped<ImportRunner>();
        return services.BuildServiceProvider();
    }
}

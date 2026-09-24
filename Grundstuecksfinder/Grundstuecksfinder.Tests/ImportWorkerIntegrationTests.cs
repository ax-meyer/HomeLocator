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
using Xunit;

namespace Grundstuecksfinder.Tests;

/// <summary>The worker's two kinds of runs against a real database.</summary>
[Collection("Postgres")]
public sealed class ImportWorkerIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task StartupRunImportsOnlyNewSources_TheNightlyRunDoesTheRoutineReimport()
    {
        await using (var context = fixture.CreateContext())
        {
            context.SourceStates.Add(ImportSeed.Serving(ImportSeed.Completed("bw", ImportSeed.Day(0), fingerprint: "1")));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var due = new StubPropertySource("bw", "1", FingerprintKind.Approximate);   // served 200 days ago
        var fresh = new StubPropertySource("sh", "1", FingerprintKind.Approximate); // never imported
        var time = new FakeTimeProvider(ImportSeed.Day(200).AddHours(2));
        var logger = new CapturingLogger<ImportWorker>();
        await using var services = Services(time, due, fresh);
        using var worker = new ImportWorker(services.GetRequiredService<IServiceScopeFactory>(), time, logger);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await Eventually.WaitUntilAsync(() => logger.MessagesAt(LogLevel.Information).Any());

        (due.Fetches, fresh.Fetches).Should().Be((0, 1), "the startup run only fills sources without data");

        await Eventually.WaitUntilAsync(() => due.Fetches == 1, () => time.Advance(TimeSpan.FromHours(1)));
        await worker.StopAsync(TestContext.Current.CancellationToken);

        fresh.Fetches.Should().Be(1);
    }

    private ServiceProvider Services(TimeProvider time, params IPropertySource[] sources)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(time);
        services.AddSingleton(fixture.DataSource);
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
        services.AddSingleton(new RefreshOptions());
        services.AddScoped(_ => new PropertyBulkWriter(fixture.DataSource, timeProvider: time));
        services.AddScoped<ImportStateStore>();
        services.AddScoped<ImportRunner>();
        foreach (var source in sources)
            services.AddSingleton(source);
        return services.BuildServiceProvider();
    }
}

using FluentAssertions;
using Grundstuecksfinder.Data;
using Grundstuecksfinder.Infrastructure;
using Grundstuecksfinder.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Grundstuecksfinder.Tests;

public sealed class DatabaseHealthCheckTests
{
    internal static Task<HealthCheckResult> CheckAsync(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new DatabaseHealthCheck(scopeFactory)
            .CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DatabaseUnreachable_IsUnhealthy()
    {
        // Port 1 on loopback: nothing listens there, so the connection is refused at once.
        var result = await CheckAsync("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=5");

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }
}

[Collection("Postgres")]
public sealed class DatabaseHealthCheckIntegrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task DatabaseReachable_IsHealthy() =>
        (await DatabaseHealthCheckTests.CheckAsync(fixture.ConnectionString)).Status.Should().Be(HealthStatus.Healthy);
}

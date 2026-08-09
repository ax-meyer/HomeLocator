using Grundstuecksfinder.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Grundstuecksfinder.Infrastructure;

public class DatabaseHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.CanConnectAsync(ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(ex.Message);
        }
    }
}

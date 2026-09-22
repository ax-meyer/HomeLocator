using Grundstuecksfinder.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Grundstuecksfinder.Services;

/// <summary>The PLZ and Gemeinde choices for the search filters.</summary>
public sealed record FilterOptions(IReadOnlyList<string> Plz, IReadOnlyList<string> Gemeinden);

/// <summary>
/// Serves the filter dropdowns from memory. Building them takes a DISTINCT over every property
/// (about a second at ~5M rows), and a prerendered page asks twice per visit, yet they only change
/// when an import swaps new rows in — which always sets a newer ImportLog.CompletedAt. So the
/// lists are cached under the latest completion time and rebuilt once after each import.
/// </summary>
public class FilterOptionsService(PropertyService properties, AppDbContext context, IMemoryCache cache)
{
    // Only keeps superseded versions from piling up; a current entry is rebuilt at most daily.
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    public async Task<FilterOptions> GetAsync()
    {
        var dataVersion = await context.ImportLogs
            .Where(l => l.CompletedAt != null)
            .MaxAsync(l => l.CompletedAt);

        return (await cache.GetOrCreateAsync($"filter-options:{dataVersion}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = MaxAge;
            return new FilterOptions(
                await properties.GetDistinctPlzAsync(),
                await properties.GetDistinctGemeindenAsync());
        }))!;
    }
}

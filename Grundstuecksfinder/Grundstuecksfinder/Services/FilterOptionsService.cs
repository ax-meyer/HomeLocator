using Grundstuecksfinder.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Grundstuecksfinder.Services;

/// <summary>The PLZ and Gemeinde choices for the search's location filter.</summary>
public sealed record FilterOptions(IReadOnlyList<SearchLocation> Locations);

/// <summary>
/// Serves the filter dropdowns from memory. Building them takes a DISTINCT over every property
/// (seconds at ~18M rows), and a prerendered page asks twice per visit, yet they only change
/// when an import swaps new rows in — which always makes a new run a source's served run. So the
/// lists are cached under the set of served runs and rebuilt once after each import.
/// </summary>
public class FilterOptionsService(PropertyService properties, AppDbContext context, IMemoryCache cache)
{
    // Only keeps superseded versions from piling up; a current entry is rebuilt at most daily.
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    // One rebuild at a time, across all circuits: every visitor arriving right after an import
    // would otherwise run the same DISTINCT at once. Static because the service is scoped.
    private static readonly SemaphoreSlim RebuildLock = new(1, 1);

    public async Task<FilterOptions> GetAsync()
    {
        // One row per source, so this stays tiny.
        var servedRuns = await context.SourceStates
            .Where(s => s.ServedRunId != null)
            .OrderBy(s => s.ServedRunId)
            .Select(s => s.ServedRunId)
            .ToListAsync();
        var key = $"filter-options:{string.Join(',', servedRuns)}";

        if (cache.TryGetValue(key, out FilterOptions? options))
            return options!;

        await RebuildLock.WaitAsync();
        try
        {
            // Whoever held the lock before may have built this very version.
            if (cache.TryGetValue(key, out options))
                return options!;

            options = new FilterOptions(SearchLocation.Combine(
                await properties.GetDistinctPlzAsync(),
                await properties.GetDistinctGemeindenAsync()));
            cache.Set(key, options, MaxAge);
            return options;
        }
        finally
        {
            RebuildLock.Release();
        }
    }
}

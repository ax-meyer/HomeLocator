using Grundstuecksfinder.Data;
using Grundstuecksfinder.Models;
using Microsoft.EntityFrameworkCore;

namespace Grundstuecksfinder.Services;

public class PropertyService(AppDbContext context, DisabledSources disabledSources)
{
    // Without disabled sources, skip the filter entirely: even an empty NOT IN can keep
    // Postgres from using index-only scans for the DISTINCT Gemeinde/PLZ lists.
    private IQueryable<Property> VisibleProperties => disabledSources.Names.Count == 0
        ? context.Properties
        : context.Properties.Where(p => !disabledSources.Names.Contains(p.Source));

    /// <summary>Visible sources that serve rows, each with the run those rows came from.</summary>
    private IQueryable<SourceState> VisibleServedSources
    {
        get
        {
            var served = context.SourceStates.Where(s => s.ServedRun != null && s.ServedRun.RecordCount > 0);
            return disabledSources.Names.Count == 0
                ? served
                : served.Where(s => !disabledSources.Names.Contains(s.Source));
        }
    }

    public async Task<List<Property>> GetPropertiesAsync(
        string? gemeinde = null,
        string? plz = null,
        double? minFlaeche = null,
        double? maxFlaeche = null,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        var query = VisibleProperties;

        if (!string.IsNullOrWhiteSpace(gemeinde))
            query = query.Where(p => p.Gemeinde == gemeinde);
        if (!string.IsNullOrWhiteSpace(plz))
            query = query.Where(p => p.Plz == plz);
        if (minFlaeche.HasValue)
            query = query.Where(p => p.FlaecheAmtl >= minFlaeche.Value);
        if (maxFlaeche.HasValue)
            query = query.Where(p => p.FlaecheAmtl <= maxFlaeche.Value);

        return await query
            .OrderBy(p => p.Str).ThenBy(p => p.Hnr)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<string>> GetDistinctGemeindenAsync() =>
        await VisibleProperties
            .Where(p => p.Gemeinde != null)
            .Select(p => p.Gemeinde!)
            .Distinct()
            .OrderBy(g => g)
            .ToListAsync();

    public async Task<List<string>> GetDistinctPlzAsync() =>
        await VisibleProperties
            .Where(p => p.Plz != null)
            .Select(p => p.Plz!)
            .Distinct()
            .OrderBy(p => p)
            .ToListAsync();

    /// <summary>
    /// Time up to which all visible data is confirmed current: each source's latest confirmation
    /// (its import, or a later run that found it within its refresh policy), and of those the
    /// oldest — a source whose checks keep failing holds the date back. Null before the first import.
    /// </summary>
    public async Task<DateTimeOffset?> GetLastCheckedAtAsync() =>
        await VisibleServedSources.MinAsync(s => s.LastCheckedAt ?? s.ServedRun!.CompletedAt);

    /// <summary>
    /// The year each source's served data was fetched, by source slug — the "Jahr des
    /// Datenbezugs" that attribution lines such as BW's and NI's must carry. Sources without
    /// served data are missing; disabled ones are included, since their credit stays listed.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int>> GetRetrievalYearsAsync()
    {
        var served = await context.SourceStates
            .Where(s => s.ServedRun != null)
            .Select(s => new { s.Source, s.ServedRun!.StartedAt })
            .ToListAsync();
        return served.ToDictionary(s => s.Source, s => s.StartedAt.ToLocalTime().Year, StringComparer.Ordinal);
    }

    /// <summary>
    /// Every import replaces its source's rows, so each source's served run holds that source's
    /// row count. Read from the small SourceStates/ImportRuns tables instead of counting
    /// millions of Properties on every page load.
    /// </summary>
    public async Task<long> GetTotalPropertyCountAsync() =>
        await VisibleServedSources.SumAsync(s => s.ServedRun!.RecordCount);
}

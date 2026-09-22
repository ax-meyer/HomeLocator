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

    private IQueryable<ImportLog> VisibleCompletedImports
    {
        get
        {
            var completed = context.ImportLogs.Where(l => l.CompletedAt != null && l.RecordCount > 0);
            return disabledSources.Names.Count == 0
                ? completed
                : completed.Where(l => !disabledSources.Names.Contains(l.Source));
        }
    }

    public async Task<List<Property>> GetPropertiesAsync(
        string? gemeinde = null,
        string? plz = null,
        double? minFlaeche = null,
        double? maxFlaeche = null,
        int limit = 500)
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
            .ToListAsync();
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
    /// Unix ms up to which all visible data is confirmed current: each source's latest successful
    /// check (an import, or a later run that found nothing newer), and of those the oldest — a
    /// source whose checks keep failing holds the date back. Null before the first import.
    /// </summary>
    public async Task<long?> GetLastCheckedAtAsync()
    {
        var checks = await VisibleCompletedImports
            .GroupBy(l => l.Source)
            .Select(g => g.Max(l => l.LastCheckedAt ?? l.CompletedAt))
            .ToListAsync();
        return checks.Count == 0 ? null : checks.Min();
    }

    /// <summary>
    /// Every import replaces its source's rows, so the latest completed import per source holds
    /// that source's row count. Read from the small ImportLogs table instead of counting millions
    /// of Properties on every page load.
    /// </summary>
    public async Task<long> GetTotalPropertyCountAsync()
    {
        var imports = await VisibleCompletedImports
            .Select(l => new { l.Source, l.CompletedAt, l.RecordCount })
            .ToListAsync();
        return imports
            .GroupBy(l => l.Source)
            .Sum(g => g.MaxBy(l => l.CompletedAt)!.RecordCount);
    }
}

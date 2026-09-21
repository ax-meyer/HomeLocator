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

    public async Task<ImportLog?> GetLastImportAsync() =>
        await VisibleCompletedImports
            .OrderByDescending(l => l.ImportedAt)
            .FirstOrDefaultAsync();

    public async Task<long> GetTotalPropertyCountAsync() =>
        await VisibleProperties.LongCountAsync();
}

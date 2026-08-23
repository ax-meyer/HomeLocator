using Grundstuecksfinder.Data;
using Grundstuecksfinder.Models;
using Microsoft.EntityFrameworkCore;

namespace Grundstuecksfinder.Services;

public class PropertyService(AppDbContext context)
{
    public async Task<List<Property>> GetPropertiesAsync(
        string? gemeinde = null,
        string? plz = null,
        double? minFlaeche = null,
        double? maxFlaeche = null,
        int limit = 500)
    {
        var query = context.Properties.AsQueryable();

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

    public async Task<List<string>> GetDistinctGemeindenAsync(string? plz = null)
    {
        var query = context.Properties
            .Where(p => p.Gemeinde != null);

        if (!string.IsNullOrWhiteSpace(plz))
            query = query.Where(p => p.Plz == plz);

        return await query
            .Select(p => p.Gemeinde!)
            .Distinct()
            .OrderBy(g => g)
            .ToListAsync();
    }

    public async Task<List<string>> GetDistinctPlzAsync() =>
        await context.Properties
            .Where(p => p.Plz != null)
            .Select(p => p.Plz!)
            .Distinct()
            .OrderBy(p => p)
            .ToListAsync();

    public async Task<ImportLog?> GetLastImportAsync() =>
        await context.ImportLogs
            .OrderByDescending(l => l.ImportedAt)
            .FirstOrDefaultAsync();
}

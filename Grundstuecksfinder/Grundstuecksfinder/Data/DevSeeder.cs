using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services.Importers;
using Grundstuecksfinder.Services.Importers.Nrw;
using Microsoft.EntityFrameworkCore;

namespace Grundstuecksfinder.Data;

public static class DevSeeder
{
    public static async Task SeedAsync(AppDbContext context)
    {
        if (await context.Properties.AnyAsync()) return;

        var csvPath = FindPartCsv();
        if (csvPath == null)
        {
            // Fall back to a minimal seed so the app starts without the file
            var fallbackLog = new ImportLog
            {
                Source = NrwPropertyImporter.SourceId,
                DatasetName = "dev-seed", FileName = "fallback", FileTimestamp = "dev",
                ImportedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), RecordCount = 1,
            };
            context.ImportLogs.Add(fallbackLog);
            context.Properties.Add(
                new Property { Str = "Hauptstraße", Hnr = "1", Plz = "50667", Ort = "Köln", Gemeinde = "Köln", FlaecheAmtl = 320, Source = NrwPropertyImporter.SourceId, ImportLog = fallbackLog });
            await context.SaveChangesAsync();
            return;
        }

        var properties = new List<Property>();
        using var reader = new StreamReader(csvPath);
        var headerSkipped = false;

        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (!headerSkipped) { headerSkipped = true; continue; }

            var p = DelimitedPropertyParser.ParseLine(line, NrwPropertyImporter.ColumnMap);
            if (p != null)
            {
                p.Source = NrwPropertyImporter.SourceId;
                properties.Add(p);
            }
        }

        var importLog = new ImportLog
        {
            Source = NrwPropertyImporter.SourceId,
            DatasetName = "dev-seed",
            FileName = Path.GetFileName(csvPath),
            FileTimestamp = "dev",
            ImportedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            RecordCount = properties.Count,
        };
        context.ImportLogs.Add(importLog);
        foreach (var p in properties) p.ImportLog = importLog;
        context.Properties.AddRange(properties);

        await context.SaveChangesAsync();
    }

    /// <summary>Walk up the directory tree from the executable to find part.csv.</summary>
    private static string? FindPartCsv()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "part.csv");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}

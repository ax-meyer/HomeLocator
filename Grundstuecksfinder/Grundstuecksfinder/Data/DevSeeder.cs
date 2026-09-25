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
            context.Properties.Add(new Property
            {
                Str = "Hauptstraße", Hnr = "1", Plz = "50667", Ort = "Köln", Gemeinde = "Köln", FlaecheAmtl = 320,
                Source = NrwPropertyImporter.SourceId, ImportRun = ServedRun(context, recordCount: 1),
            });
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

        var run = ServedRun(context, properties.Count);
        foreach (var p in properties) p.ImportRun = run;
        context.Properties.AddRange(properties);

        await context.SaveChangesAsync();
    }

    /// <summary>
    /// A completed run serving the seeded rows, as a real import would leave it. Its fingerprint
    /// matches no real version, so the first import run replaces the seed with real data.
    /// </summary>
    private static ImportRun ServedRun(AppDbContext context, long recordCount)
    {
        var now = DateTimeOffset.UtcNow;
        var run = new ImportRun
        {
            Source = NrwPropertyImporter.SourceId, Fingerprint = "dev-seed", Reason = ImportReason.Initial,
            StartedAt = now, CompletedAt = now, RecordCount = recordCount,
        };
        context.SourceStates.Add(new SourceState { Source = NrwPropertyImporter.SourceId, ServedRun = run, LastCheckedAt = now });
        return run;
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

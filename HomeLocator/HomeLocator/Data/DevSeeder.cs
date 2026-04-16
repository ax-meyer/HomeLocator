using HomeLocator.Models;
using HomeLocator.Services;
using Microsoft.EntityFrameworkCore;

namespace HomeLocator.Data;

public static class DevSeeder
{
    public static async Task SeedAsync(AppDbContext context)
    {
        if (await context.Properties.AnyAsync()) return;

        var csvPath = FindPartCsv();
        if (csvPath == null)
        {
            // Fall back to a minimal seed so the app starts without the file
            context.Properties.Add(
                new Property { Str = "Hauptstraße", Hnr = "1", Plz = "50667", Ort = "Köln", Gemeinde = "Köln", FlaecheAmtl = 320 });
            context.ImportLogs.Add(new ImportLog
            {
                DatasetName = "dev-seed", FileName = "fallback", FileTimestamp = "dev",
                ImportedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), RecordCount = 1,
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

            var p = CsvParser.ParseLine(line);
            if (p != null) properties.Add(p);
        }

        context.Properties.AddRange(properties);
        context.ImportLogs.Add(new ImportLog
        {
            DatasetName = "dev-seed",
            FileName = Path.GetFileName(csvPath),
            FileTimestamp = "dev",
            ImportedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            RecordCount = properties.Count,
        });

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

using System.Globalization;

namespace Grundstuecksfinder.Services.Importers.Scheduling;

/// <summary>
/// "Import:Refresh": when sources are re-imported. The ages are defaults every source can
/// override in its own "Refresh" section (see <see cref="RefreshOverride"/>).
/// </summary>
public sealed class RefreshOptions
{
    public const string SectionName = "Import:Refresh";

    /// <summary>Default <see cref="RefreshPolicy.MinAge"/>, in days.</summary>
    public double MinAgeDays { get; set; } = RefreshPolicy.Default.MinAge.TotalDays;

    /// <summary>Default <see cref="RefreshPolicy.MaxAge"/>, in days.</summary>
    public double MaxAgeDays { get; set; } = RefreshPolicy.Default.MaxAge.TotalDays;

    /// <summary>
    /// Re-imports of already served sources per nightly run, most overdue first; the rest wait
    /// for the next night. Staggers the large states (hours each) instead of running them all
    /// in one night. Sources without served data are never held back by this. 0 pauses routine
    /// re-imports.
    /// </summary>
    public int MaxRoutineImportsPerRun { get; set; } = 1;

    /// <summary>The effective policy of a source: its override where set, these defaults elsewhere.</summary>
    public RefreshPolicy PolicyFor(RefreshOverride? sourceOverride) => new(
        TimeSpan.FromDays(sourceOverride?.MinAgeDays ?? MinAgeDays),
        TimeSpan.FromDays(sourceOverride?.MaxAgeDays ?? MaxAgeDays));

    /// <summary>
    /// Checks the defaults and every source's effective policy; returns one message per problem.
    /// Run at startup so a broken config fails the deploy instead of the nightly import.
    /// </summary>
    public IReadOnlyList<string> Validate(IReadOnlyDictionary<string, RefreshOverride?> overridesBySource)
    {
        var errors = new List<string>();
        if (MaxRoutineImportsPerRun < 0)
            errors.Add($"{SectionName}: MaxRoutineImportsPerRun must not be negative.");

        ValidatePolicy(SectionName, MinAgeDays, MaxAgeDays, errors);
        foreach (var (source, sourceOverride) in overridesBySource.OrderBy(o => o.Key, StringComparer.Ordinal))
        {
            if (sourceOverride is null) continue;
            ValidatePolicy($"{source}: Refresh", sourceOverride.MinAgeDays ?? MinAgeDays, sourceOverride.MaxAgeDays ?? MaxAgeDays, errors);
        }
        return errors;
    }

    private static void ValidatePolicy(string name, double minAgeDays, double maxAgeDays, List<string> errors)
    {
        if (!(minAgeDays >= 0))
            errors.Add($"{name}: MinAgeDays must not be negative.");
        else if (!(maxAgeDays >= minAgeDays))
            errors.Add(string.Create(CultureInfo.InvariantCulture,
                $"{name}: MaxAgeDays ({maxAgeDays}) must be at least MinAgeDays ({minAgeDays})."));
    }
}

/// <summary>
/// A source's own "Refresh" section, e.g. "Import:Nrw:Refresh" or an INSPIRE source's
/// "Refresh"; an unset value falls back to <see cref="RefreshOptions"/>.
/// </summary>
public sealed class RefreshOverride
{
    public double? MinAgeDays { get; set; }
    public double? MaxAgeDays { get; set; }
}

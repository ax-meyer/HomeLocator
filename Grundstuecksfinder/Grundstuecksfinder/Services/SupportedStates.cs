namespace Grundstuecksfinder.Services;

/// <summary>
/// Bundesländer whose data the app shows, for the front page. Built from the enabled sources,
/// so switching a source on or off in config updates the list without a code change.
/// </summary>
public sealed record SupportedStates(IReadOnlyList<string> Names)
{
    private static readonly Dictionary<string, string> NamesBySource = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bw"] = "Baden-Württemberg",
        ["by"] = "Bayern",
        ["be"] = "Berlin",
        ["bb"] = "Brandenburg",
        ["hb"] = "Bremen",
        ["hh"] = "Hamburg",
        ["he"] = "Hessen",
        ["mv"] = "Mecklenburg-Vorpommern",
        ["ni"] = "Niedersachsen",
        ["nrw"] = "Nordrhein-Westfalen",
        ["rp"] = "Rheinland-Pfalz",
        ["sl"] = "Saarland",
        ["sn"] = "Sachsen",
        ["st"] = "Sachsen-Anhalt",
        ["sh"] = "Schleswig-Holstein",
        ["th"] = "Thüringen",
    };

    /// <summary>Maps source slugs to Bundesland names, sorted; an unknown slug is shown as-is.</summary>
    public static SupportedStates FromSources(IEnumerable<string> sources) =>
        new(sources
            .Select(s => NamesBySource.GetValueOrDefault(s, s))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList());
}

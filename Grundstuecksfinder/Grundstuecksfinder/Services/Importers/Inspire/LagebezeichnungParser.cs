using System.Text.RegularExpressions;

namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>One address a parcel's Lagebezeichnung names.</summary>
public sealed record LagebezeichnungAddress(string Str, string Hnr, string? HnrZus);

/// <summary>
/// What <see cref="LagebezeichnungParser.Parse"/> read from one text: the addresses, and how
/// many parts of it looked like an address (they contain a digit) but couldn't be read.
/// </summary>
public sealed record LagebezeichnungText(IReadOnlyList<LagebezeichnungAddress> Addresses, int UnreadParts)
{
    public static readonly LagebezeichnungText Empty = new([], 0);
}

/// <summary>
/// Reads the addresses out of an ALKIS-vereinfacht "lagebeztxt": the parcel's
/// Lagebezeichnungen, streets separated by ";" and each street's house numbers by ",", e.g.
/// "Löwenhofstraße 5; Vordere Synagogenstraße 2", "Rheinstraße 105, 107",
/// "Hintere Christofsgasse 3, 3 A" or "Hospitalplatz 15a".
/// </summary>
/// <remarks>
/// A part without house numbers is a street or place the parcel merely lies on ("Steingasse",
/// "Kleingartenanlage") and names no address. What the grammar can't read is skipped rather
/// than guessed at: field names with numbers in them ("Kurze 5 Morgen", "Ablaßlache
/// 1.Gewanne"), road descriptions ("Kreisstraße von Harbke zur K1656"), and road numbers that
/// look like a street and a house number ("B 40": a "street" of one letter).
/// </remarks>
public static partial class LagebezeichnungParser
{
    public static LagebezeichnungText Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return LagebezeichnungText.Empty;

        var addresses = new List<LagebezeichnungAddress>();
        var unread = 0;
        foreach (var raw in text.Split(';'))
        {
            var part = raw.Trim();
            var match = StreetWithNumbers().Match(part);
            if (!match.Success || match.Groups["street"].Value.Count(char.IsLetter) < 2)
            {
                if (part.Any(char.IsAsciiDigit)) unread++;
                continue;
            }

            var street = match.Groups["street"].Value;
            foreach (var token in match.Groups["numbers"].Value.Split(','))
            {
                var number = HouseNumber().Match(token.Trim());
                var suffix = number.Groups["suffix"].Value;
                var address = new LagebezeichnungAddress(street, number.Groups["number"].Value, suffix.Length == 0 ? null : suffix);
                if (!addresses.Contains(address)) addresses.Add(address);
            }
        }
        return new LagebezeichnungText(addresses, unread);
    }

    /// <summary>
    /// A street, then one or more house numbers to the end. The street is matched lazily, so it
    /// ends before the first number that starts a valid list: "Straße des 17. Juni 5" keeps its
    /// "17." because ". Juni 5" is no number list.
    /// </summary>
    [GeneratedRegex(@"^(?<street>\S.*?)\s+(?<numbers>\d+ ?[A-Za-z]?(?:\s*,\s*\d+ ?[A-Za-z]?)*)$")]
    private static partial Regex StreetWithNumbers();

    [GeneratedRegex(@"^(?<number>\d+) ?(?<suffix>[A-Za-z]?)$")]
    private static partial Regex HouseNumber();
}

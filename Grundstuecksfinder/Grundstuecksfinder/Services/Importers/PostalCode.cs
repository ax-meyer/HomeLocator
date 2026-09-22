namespace Grundstuecksfinder.Services.Importers;

public static class PostalCode
{
    /// <summary>
    /// <paramref name="value"/> trimmed if it's a German PLZ (five digits), else null. Sources
    /// put other things there too: BB has street names, towns and years in postCode.
    /// </summary>
    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return trimmed is { Length: 5 } && trimmed.All(char.IsAsciiDigit) ? trimmed : null;
    }
}

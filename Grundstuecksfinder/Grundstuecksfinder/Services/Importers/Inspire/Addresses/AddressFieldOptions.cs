namespace Grundstuecksfinder.Services.Importers.Inspire.Addresses;

/// <summary>
/// "Fields" of an <see cref="AddressSourceType.FlatWfs"/> address source: which child element of
/// an address feature holds what. Street and house number are required; any other field left
/// out reads as missing.
/// </summary>
public class AddressFieldOptions
{
    public string Street { get; set; } = string.Empty;

    public string HouseNumber { get; set; } = string.Empty;

    /// <summary>The house number's suffix ("a" in "12a").</summary>
    public string? HouseNumberSuffix { get; set; }

    /// <summary>The PLZ; without one, set the source's FillMissingPlzFromPostcodeAreas.</summary>
    public string? Plz { get; set; }

    /// <summary>The place or Ortsteil name shown with the address; falls back to the Gemeinde.</summary>
    public string? Ort { get; set; }

    /// <summary>The Gemeinde's name.</summary>
    public string? Gemeinde { get; set; }

    /// <summary>One message per problem, each starting with the offending property's name.</summary>
    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Street))
            yield return "Street must name the element holding the street.";
        if (string.IsNullOrWhiteSpace(HouseNumber))
            yield return "HouseNumber must name the element holding the house number.";
        foreach (var (name, value) in new[] { ("HouseNumberSuffix", HouseNumberSuffix), ("Plz", Plz), ("Ort", Ort), ("Gemeinde", Gemeinde) })
        {
            if (value is not null && string.IsNullOrWhiteSpace(value))
                yield return $"{name} must not be blank; leave it out to read none.";
        }
    }
}

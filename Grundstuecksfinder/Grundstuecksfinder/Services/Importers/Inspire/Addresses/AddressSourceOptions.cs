namespace Grundstuecksfinder.Services.Importers.Inspire.Addresses;

/// <summary>How a source's addresses are fetched; picks the <see cref="IAddressProvider"/>.</summary>
public enum AddressSourceType
{
    /// <summary>The state's INSPIRE ad:Address WFS, one request per parcel tile.</summary>
    InspireWfs,

    /// <summary>The state's INSPIRE ad:Address WFS, paged by startIndex in one pass (Hamburg).</summary>
    InspireWfsStartIndex,

    /// <summary>An OGC API Features "items" endpoint with the ALKIS Hauskoordinaten schema (Saarland).</summary>
    OgcApiFeatures,
}

/// <summary>"AddressSource" of an "Import:Inspire:Sources" entry.</summary>
public class AddressSourceOptions
{
    public AddressSourceType Type { get; set; } = AddressSourceType.InspireWfs;

    /// <summary>
    /// The WFS base URL for <see cref="AddressSourceType.InspireWfs"/> and
    /// <see cref="AddressSourceType.InspireWfsStartIndex"/>; the collection's "items" endpoint
    /// for <see cref="AddressSourceType.OgcApiFeatures"/>.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="AddressSourceType.OgcApiFeatures"/>: features requested per page. The server
    /// may only accept specific values (Saarland: 1, 5, 10, 20, 50, 100, 200, 500, 1000, 2500) —
    /// check live before changing this. The WFS types use the source's PageSize instead.
    /// </summary>
    public int OgcApiPageSize { get; set; } = 2500;

    /// <summary>One message per problem, each starting with the offending property's name.</summary>
    public IEnumerable<string> Validate()
    {
        if (!Enum.IsDefined(Type))
            yield return $"Type must be one of {string.Join(", ", Enum.GetNames<AddressSourceType>())}.";
        if (!InspireSourceOptions.IsHttpUrl(Url))
            yield return "Url must be an absolute http(s) URL.";
        if (Type == AddressSourceType.OgcApiFeatures && OgcApiPageSize < 1)
            yield return "OgcApiPageSize must be positive.";
    }
}

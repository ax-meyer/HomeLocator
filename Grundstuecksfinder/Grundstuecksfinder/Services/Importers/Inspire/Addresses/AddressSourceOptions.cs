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

    /// <summary>A statewide Hauskoordinaten text file in a ZIP (Baden-Württemberg, Hessen).</summary>
    HkFile,
}

/// <summary>How an <see cref="AddressSourceType.HkFile"/> source finds its current file.</summary>
public enum HkFileLocatorType
{
    /// <summary>A fixed URL; its ETag/Last-Modified is the version (<see cref="StaticUrlHkFileLocator"/>).</summary>
    StaticUrl,

    /// <summary>A product folder of Hessen's download center (<see cref="HessenDownloadCenterLocator"/>).</summary>
    HessenDownloadCenter,
}

/// <summary>"AddressSource" of an "Import:Inspire:Sources" entry.</summary>
public class AddressSourceOptions
{
    /// <summary>
    /// What <see cref="AllowedQualities"/> defaults to: "A" and "B" are addresses placed on a
    /// building. BW's quality "C" rows carry synthetic house numbers made up from their
    /// coordinates ("Distr. Am Ottersberg 86188851"), which nobody lives at.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultQualities = ["A", "B"];

    public AddressSourceType Type { get; set; } = AddressSourceType.InspireWfs;

    /// <summary>
    /// The WFS base URL for <see cref="AddressSourceType.InspireWfs"/> and
    /// <see cref="AddressSourceType.InspireWfsStartIndex"/>; the collection's "items" endpoint
    /// for <see cref="AddressSourceType.OgcApiFeatures"/>; for
    /// <see cref="AddressSourceType.HkFile"/> the file itself, or with
    /// <see cref="HkFileLocatorType.HessenDownloadCenter"/> the REST listing of its folder.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="AddressSourceType.OgcApiFeatures"/>: features requested per page. The server
    /// may only accept specific values (Saarland: 1, 5, 10, 20, 50, 100, 200, 500, 1000, 2500) —
    /// check live before changing this. The WFS types use the source's PageSize instead.
    /// </summary>
    public int OgcApiPageSize { get; set; } = 2500;

    /// <summary><see cref="AddressSourceType.HkFile"/>: how <see cref="Url"/> leads to the file.</summary>
    public HkFileLocatorType Locator { get; set; } = HkFileLocatorType.StaticUrl;

    /// <summary>
    /// <see cref="AddressSourceType.HkFile"/>: the ZIP entry to read — its name, or a pattern with
    /// * and ? when the name carries the edition. Exactly one entry must match.
    /// </summary>
    public string Member { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="AddressSourceType.HkFile"/>: the "qua" values to import; see
    /// <see cref="DefaultQualities"/>. Nullable rather than defaulted, because the config binder
    /// appends a configured list to a default one instead of replacing it.
    /// </summary>
    public string[]? AllowedQualities { get; set; }

    /// <summary><see cref="AllowedQualities"/>, or <see cref="DefaultQualities"/> if unset.</summary>
    public IReadOnlyList<string> Qualities => AllowedQualities ?? DefaultQualities;

    /// <summary>
    /// <see cref="AddressSourceType.HkFile"/>: limit for one download attempt, body included —
    /// far more than one WFS page's RequestTimeoutSeconds (BW's file is 73 MB).
    /// </summary>
    public double DownloadTimeoutSeconds { get; set; } = 1800;

    /// <summary>Largest <see cref="OgcApiPageSize"/>: OGC API Features servers commonly cap a page at 10,000.</summary>
    public const int MaxOgcApiPageSize = 10_000;

    /// <summary>
    /// Longest <see cref="DownloadTimeoutSeconds"/>: a day, far beyond any real download, and
    /// far inside what a TimeSpan can hold.
    /// </summary>
    public const double MaxDownloadTimeoutSeconds = 86_400;

    /// <summary>One message per problem, each starting with the offending property's name.</summary>
    public IEnumerable<string> Validate()
    {
        if (!Enum.IsDefined(Type))
            yield return $"Type must be one of {string.Join(", ", Enum.GetNames<AddressSourceType>())}.";
        if (!InspireSourceOptions.IsHttpUrl(Url))
            yield return "Url must be an absolute http(s) URL.";
        if (Type == AddressSourceType.OgcApiFeatures && OgcApiPageSize is < 1 or > MaxOgcApiPageSize)
            yield return FormattableString.Invariant($"OgcApiPageSize must be between 1 and {MaxOgcApiPageSize}.");
        if (Type != AddressSourceType.HkFile) yield break;

        if (!Enum.IsDefined(Locator))
            yield return $"Locator must be one of {string.Join(", ", Enum.GetNames<HkFileLocatorType>())}.";
        if (string.IsNullOrWhiteSpace(Member))
            yield return "Member must name the ZIP entry to read.";
        if (AllowedQualities is { } qualities && (qualities.Length == 0 || qualities.Any(string.IsNullOrWhiteSpace)))
            yield return "AllowedQualities must list at least one quality, and no blank ones.";
        if (!(DownloadTimeoutSeconds is > 0 and <= MaxDownloadTimeoutSeconds))
            yield return FormattableString.Invariant($"DownloadTimeoutSeconds must be greater than 0 and at most {MaxDownloadTimeoutSeconds}.");
    }
}

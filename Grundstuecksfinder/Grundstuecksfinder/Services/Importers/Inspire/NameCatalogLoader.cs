using System.Xml;
using System.Xml.Linq;

namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>
/// Builds a <see cref="NameCatalog"/> for Hessen from the two datasets that still carry the
/// intact spellings, both published by the same authority (HVBG) as the address service:
/// INSPIRE Administrative Units (from ATKIS Basis-DLM) for the Gemeinden, and the ALKIS
/// street-name catalogue (AX_LagebezeichnungKatalogeintrag) for the streets.
/// </summary>
/// <remarks>
/// The keys line up with the address service's component ids: an administrative unit's
/// nationalCode is the AGS the service writes as "AdminUnitName_&lt;AGS&gt;", and a catalogue
/// entry's schluesselGesamt is that AGS followed by the street key, which the service writes as
/// "ThoroughfareName_&lt;AGS&gt;&lt;street key, 5 digits&gt;".
/// </remarks>
public sealed partial class NameCatalogLoader(
    ILogger<NameCatalogLoader> logger,
    IHttpClientFactory httpClientFactory)
{
    private const string AdminUnitType = "au:AdministrativeUnit";
    private const string StreetCatalogueType = "adv:AX_LagebezeichnungKatalogeintrag";

    /// <summary>Entries per request; both services answer a page of this size in about a second.</summary>
    private const int PageSize = 5000;

    public async Task<NameCatalog> LoadAsync(NameCatalogOptions options, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient(InspirePropertyImporter.HttpClientName);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        var units = await ReadAsync(http, options.AdminUnitWfsUrl, AdminUnitType, options.RequestTimeoutSeconds,
            element =>
            {
                var code = Text(element, "nationalCode");
                var name = Text(element, "text");
                return code is null || name is null ? null : ($"AdminUnitName_{code}", name);
            }, names, ct);

        var streets = await ReadAsync(http, options.StreetCatalogueWfsUrl, StreetCatalogueType, options.RequestTimeoutSeconds,
            element =>
            {
                var key = Text(element, "schluesselGesamt");
                var name = Text(element, "bezeichnung");
                return key is null || name is null || key.Length <= 8 ? null : (ThoroughfareId(key), name);
            }, names, ct);

        LogLoaded(logger, names.Count, units, streets);
        if (names.Count == 0)
            throw new InspireImportException(
                "The name catalogue came back empty; the addresses would keep their damaged names.");

        return new NameCatalog(names);
    }

    /// <summary>
    /// "06435003" + "233" (the catalogue's own key) becomes "ThoroughfareName_0643500300233":
    /// the AGS, then the street key padded to the five digits the address service writes.
    /// </summary>
    private static string ThoroughfareId(string schluesselGesamt)
    {
        var ags = schluesselGesamt[..8];
        var street = schluesselGesamt[8..].TrimStart('0');
        return FormattableString.Invariant($"ThoroughfareName_{ags}{street.PadLeft(5, '0')}");
    }

    /// <summary>
    /// Pages through a feature type, keeping only names that contain a non-ASCII character:
    /// those are exactly the ones the address service could have damaged, and skipping the rest
    /// keeps a statewide catalogue small enough to hold in memory.
    /// </summary>
    private static async Task<int> ReadAsync(
        HttpClient http, string baseUrl, string typeName, double timeoutSeconds,
        Func<XElement, (string Id, string Name)?> read, Dictionary<string, string> into, CancellationToken ct)
    {
        var kept = 0;
        for (var startIndex = 0; ; startIndex += PageSize)
        {
            var url = FormattableString.Invariant(
                $"{baseUrl}?service=WFS&version=2.0.0&request=GetFeature&typenames={Uri.EscapeDataString(typeName)}&count={PageSize}&startIndex={startIndex}");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            XDocument document;
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
            {
                if (!response.IsSuccessStatusCode)
                    throw new InspireImportException(
                        FormattableString.Invariant($"{typeName}: the name catalogue answered {(int)response.StatusCode} for {url}."));

                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var reader = XmlReader.Create(stream, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit });
                document = await XDocument.LoadAsync(reader, LoadOptions.None, timeout.Token);
            }

            var members = document.Root?.Elements().Where(e => e.Name.LocalName == "member").ToList() ?? [];
            foreach (var entry in members.Select(m => m.Elements().FirstOrDefault()).OfType<XElement>().Select(read).OfType<(string Id, string Name)>())
            {
                // An all-ASCII name is identical in both datasets, so it can never need repairing.
                if (entry.Name.Any(c => !char.IsAscii(c)) && into.TryAdd(entry.Id, entry.Name)) kept++;
            }

            if (members.Count < PageSize) return kept;
        }
    }

    private static string? Text(XElement element, string localName)
    {
        var value = element.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Name catalogue: {Total} spellings to repair from ({Units} administrative units, {Streets} streets)")]
    private static partial void LogLoaded(ILogger logger, int total, int units, int streets);
}

/// <summary>Where a source's intact name spellings come from; see <see cref="NameCatalogLoader"/>.</summary>
public class NameCatalogOptions
{
    /// <summary>INSPIRE Administrative Units WFS, whose nationalCode is the AGS.</summary>
    public string AdminUnitWfsUrl { get; set; } = string.Empty;

    /// <summary>ALKIS WFS carrying AX_LagebezeichnungKatalogeintrag, the street-name catalogue.</summary>
    public string StreetCatalogueWfsUrl { get; set; } = string.Empty;

    /// <summary>Limit for one catalogue request including reading and parsing it.</summary>
    public double RequestTimeoutSeconds { get; set; } = 300;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AdminUnitWfsUrl) && !string.IsNullOrWhiteSpace(StreetCatalogueWfsUrl);
}

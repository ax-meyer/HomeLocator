using System.Runtime.CompilerServices;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services.Importers.Inspire.Addresses;
using Grundstuecksfinder.Services.Importers.Postcodes;
using Grundstuecksfinder.Services.Importers.Scheduling;

namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>
/// Imports an INSPIRE-split Bundesland: parcels (area + geometry) from its parcel WFS, addresses
/// (text + point) from the <see cref="IAddressProvider"/> its
/// <see cref="InspireSourceOptions.AddressSource"/> names, joined by <see cref="ParcelAddressJoin"/>
/// — or, where the parcels name their own addresses
/// (<see cref="AddressSourceType.ParcelLagebezeichnung"/>), the parcels alone, read by
/// <see cref="ParcelLagebezeichnungImport"/>. One <see cref="InspireSourceOptions"/> instance =
/// one state; this class only wires the parts together.
/// </summary>
/// <remarks>
/// Tiling over the state's bounding box happens inside <see cref="FetchAsync"/> only to bound
/// memory — a state is one source with one fingerprint, because <see cref="PropertyBulkWriter"/>
/// replaces all of a source's rows per write.
/// </remarks>
public sealed class InspirePropertyImporter : IPropertySource
{
    /// <summary>Named HttpClient for every request of these sources; per-request limits come from the options.</summary>
    public const string HttpClientName = "Inspire";

    private readonly ILogger<InspirePropertyImporter> _logger;
    private readonly InspireSourceOptions _options;
    private readonly IPostcodeAreaProvider? _postcodeAreas;
    private readonly InspireParcelProvider _parcels;
    /// <summary>Null for <see cref="AddressSourceType.ParcelLagebezeichnung"/>: nothing to join.</summary>
    private readonly IAddressProvider? _addresses;

    public InspirePropertyImporter(
        ILogger<InspirePropertyImporter> logger,
        IHttpClientFactory httpClientFactory,
        InspireSourceOptions options,
        TimeProvider? timeProvider = null,
        IPostcodeAreaProvider? postcodeAreas = null,
        ILoggerFactory? loggerFactory = null,
        RefreshPolicy? refreshPolicy = null,
        string? workDirectory = null)
    {
        _logger = logger;
        _options = options;
        _postcodeAreas = postcodeAreas;
        RefreshPolicy = refreshPolicy ?? RefreshPolicy.Default;

        // One client per source: all of its requests share one pace and one circuit breaker.
        var client = new InspireServiceClient(
            httpClientFactory.CreateClient(HttpClientName), options, logger, timeProvider ?? TimeProvider.System, loggerFactory);
        var parcelType = options.ParcelFeatureType;
        _parcels = new InspireParcelProvider(
            new WfsFeatureType(client, options, logger, options.ParcelWfsUrl, parcelType.TypeName, parcelType.Namespace),
            parcelType);
        _addresses = CreateAddressProvider(client, ImportWorkDirectory.Resolve(workDirectory));
    }

    public string Id => _options.Source;

    public RefreshPolicy RefreshPolicy { get; }

    private IAddressProvider? CreateAddressProvider(InspireServiceClient client, string workDirectory)
    {
        var source = _options.AddressSource;
        return source.Type switch
        {
            AddressSourceType.ParcelLagebezeichnung => null,
            AddressSourceType.InspireWfs => new InspireWfsAddressProvider(AddressWfs(), _options),
            AddressSourceType.InspireWfsStartIndex => new InspireWfsStartIndexAddressProvider(AddressWfs(), _options, _logger),
            AddressSourceType.OgcApiFeatures => new OgcApiFeaturesAddressProvider(client, _options, _logger),
            AddressSourceType.HkFile => new HkFileAddressProvider(client, _options, HkFileLocator(), workDirectory, _logger),
            _ => throw new InvalidOperationException($"{Id}: unknown AddressSource.Type {source.Type}."),
        };

        WfsFeatureType AddressWfs() => new(client, _options, _logger, source.Url, WfsFeatureType.AddressType);

        IHkFileLocator HkFileLocator() => source.Locator switch
        {
            HkFileLocatorType.StaticUrl => new StaticUrlHkFileLocator(client, source.Url),
            HkFileLocatorType.HessenDownloadCenter => new HessenDownloadCenterLocator(client, source.Url),
            _ => throw new InvalidOperationException($"{Id}: unknown AddressSource.Locator {source.Locator}."),
        };
    }

    /// <summary>
    /// The cheap checks of both sides, combined: the parcel part first, then the address part
    /// (see <see cref="InspireProbe"/>); the parcel part alone where there is no address side.
    /// Downloads no data. Throws when a side has nothing importable, e.g. a service that reports
    /// no feature count.
    /// </summary>
    public async Task<SourceProbe> ProbeAsync(CancellationToken ct)
    {
        var parcels = await _parcels.ProbeAsync(ct);
        return _addresses is null
            ? InspireProbe.ParcelsOnly(parcels)
            : InspireProbe.Combine(parcels, await _addresses.ProbeAsync(ct));
    }

    /// <summary>
    /// Fetches and joins the whole state; tiles given up on are reported to <paramref name="run"/>.
    /// The probe's address part goes to the address provider, so a file provider can tell
    /// whether its edition changed since (see <see cref="IAddressProvider.LoadAsync"/>); if it
    /// did, the run is told the fingerprint of what was really imported.
    /// </summary>
    public async IAsyncEnumerable<Property> FetchAsync(SourceProbe probe, ImportRunContext run, [EnumeratorCancellation] CancellationToken ct)
    {
        if (probe is not InspireProbe inspireProbe)
            throw new ArgumentException($"Expected the probe of {nameof(InspirePropertyImporter)}, got {probe.GetType().Name}.", nameof(probe));

        var rows = _addresses is null
            ? new ParcelLagebezeichnungImport(_options, _logger, _parcels, _postcodeAreas).RunAsync(run, ct)
            : new ParcelAddressJoin(_options, _logger, _parcels, _addresses, _postcodeAreas).RunAsync(inspireProbe, run, ct);
        await foreach (var property in rows)
            yield return property;
    }
}

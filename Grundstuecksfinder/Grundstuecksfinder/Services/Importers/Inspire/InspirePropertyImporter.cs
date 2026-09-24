using System.Runtime.CompilerServices;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services.Importers.Inspire.Addresses;
using Grundstuecksfinder.Services.Importers.Postcodes;
using Grundstuecksfinder.Services.Importers.Scheduling;

namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>
/// Imports an INSPIRE-split Bundesland: parcels (area + geometry) from its INSPIRE
/// cp:CadastralParcel WFS, addresses (text + point) from the <see cref="IAddressProvider"/> its
/// <see cref="InspireSourceOptions.AddressSource"/> names, joined by <see cref="ParcelAddressJoin"/>.
/// One <see cref="InspireSourceOptions"/> instance = one state; this class only wires the parts
/// together.
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
    private readonly IAddressProvider _addresses;

    public InspirePropertyImporter(
        ILogger<InspirePropertyImporter> logger,
        IHttpClientFactory httpClientFactory,
        InspireSourceOptions options,
        TimeProvider? timeProvider = null,
        IPostcodeAreaProvider? postcodeAreas = null,
        NameCatalogLoader? nameCatalogLoader = null,
        ILoggerFactory? loggerFactory = null,
        RefreshPolicy? refreshPolicy = null)
    {
        _logger = logger;
        _options = options;
        _postcodeAreas = postcodeAreas;
        RefreshPolicy = refreshPolicy ?? RefreshPolicy.Default;

        // One client per source: all of its requests share one pace and one circuit breaker.
        var client = new InspireServiceClient(
            httpClientFactory.CreateClient(HttpClientName), options, logger, timeProvider ?? TimeProvider.System, loggerFactory);
        _parcels = new InspireParcelProvider(
            new WfsFeatureType(client, options, logger, options.ParcelWfsUrl, WfsFeatureType.ParcelType));
        _addresses = CreateAddressProvider(client, nameCatalogLoader);
    }

    public string Id => _options.Source;

    public RefreshPolicy RefreshPolicy { get; }

    private IAddressProvider CreateAddressProvider(InspireServiceClient client, NameCatalogLoader? nameCatalogLoader)
    {
        var source = _options.AddressSource;
        return source.Type switch
        {
            AddressSourceType.InspireWfs => new InspireWfsAddressProvider(AddressWfs(), _options, _logger, nameCatalogLoader),
            AddressSourceType.InspireWfsStartIndex => new InspireWfsStartIndexAddressProvider(AddressWfs(), _options, _logger, nameCatalogLoader),
            AddressSourceType.OgcApiFeatures => new OgcApiFeaturesAddressProvider(client, _options, _logger),
            _ => throw new InvalidOperationException($"{Id}: unknown AddressSource.Type {source.Type}."),
        };

        WfsFeatureType AddressWfs() => new(client, _options, _logger, source.Url, WfsFeatureType.AddressType);
    }

    /// <summary>
    /// The cheap checks of both sides, combined: the parcel part first, then the address part
    /// (see <see cref="InspireProbe"/>). Downloads no data. Throws when a side has nothing
    /// importable, e.g. a service that reports no feature count.
    /// </summary>
    public async Task<SourceProbe> ProbeAsync(CancellationToken ct) =>
        InspireProbe.Combine(await _parcels.ProbeAsync(ct), await _addresses.ProbeAsync(ct));

    /// <summary>
    /// Fetches and joins the whole state; tiles given up on are reported to <paramref name="run"/>.
    /// </summary>
    public async IAsyncEnumerable<Property> FetchAsync(SourceProbe probe, ImportRunContext run, [EnumeratorCancellation] CancellationToken ct)
    {
        if (probe is not InspireProbe)
            throw new ArgumentException($"Expected the probe of {nameof(InspirePropertyImporter)}, got {probe.GetType().Name}.", nameof(probe));

        var join = new ParcelAddressJoin(_options, _logger, _parcels, _addresses, _postcodeAreas);
        await foreach (var property in join.RunAsync(run, ct))
            yield return property;
    }
}

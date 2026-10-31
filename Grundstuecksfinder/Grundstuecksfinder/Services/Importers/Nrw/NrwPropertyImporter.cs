using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services.Importers.Inspire;
using Grundstuecksfinder.Services.Importers.Postcodes;
using Grundstuecksfinder.Services.Importers.Scheduling;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace Grundstuecksfinder.Services.Importers.Nrw;

/// <summary>
/// Imports NRW's open-data "Grundsteuer" dataset: a JSON manifest listing one ZIP of
/// semicolon-CSVs per yearly edition, each with its publication timestamp. The CSV has no
/// coordinates, so each row's map position is looked up by address in the Gebäudereferenzen
/// (<see cref="GebrefIndex"/>), downloaded first.
/// </summary>
/// <remarks>
/// <paramref name="postcodeAreas"/> tells apart the addresses GEBREF lists at several points
/// (Duisburg's duplicate street names); without it those rows stay without a position.
/// </remarks>
public partial class NrwPropertyImporter(
    ILogger<NrwPropertyImporter> logger,
    IHttpClientFactory httpClientFactory,
    IOptions<NrwImporterOptions> options,
    TimeProvider? timeProvider = null,
    string? workDirectory = null,
    IPostcodeAreaProvider? postcodeAreas = null) : IPropertySource
{
    public const string SourceId = "nrw";

    /// <summary>
    /// What the imported rows contain, as part of the fingerprint: the manifest's timestamps are
    /// Exact, so new code alone never re-imports NRW; bumping this does, once. r2: rows the
    /// address alone can't place get their parcel's Lagebezeichnung or their PLZ.
    /// </summary>
    public const string Revision = "r2";

    /// <summary>Named HttpClient for the manifest and the ZIP; per-attempt limits come from the options.</summary>
    public const string HttpClientName = "Nrw";

    /// <summary>
    /// The download's file name in the work directory. Fixed rather than unique, so a download
    /// left behind by a crashed run is found and replaced instead of piling up; only one NRW
    /// import runs at a time (see <see cref="PropertyBulkWriter"/>'s source lock).
    /// </summary>
    public const string DownloadFileName = "nrw-grundsteuer.zip";

    /// <summary>The GEBREF download's file name in the work directory, fixed for the same reason.</summary>
    public const string GebrefDownloadFileName = "nrw-gebref.zip";

    // The Grundsteuer CSV's placeholder where the cadastre's address part isn't unique: a
    // parcel naming several streets has it as its street, several numbers as its number, several
    // suffixes as its suffix.
    private const string Unclear = "uneindeutig";

    // The parcel's whole Lagebezeichnung, e.g. "Im Wiesengrund 3; Kupferstraße 9" where str and
    // hnr say "uneindeutig"; the column map doesn't cover it.
    private const int LagebezeichnungIndex = 3;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly string _workDirectory = ImportWorkDirectory.Resolve(workDirectory);
    private ResiliencePipeline? _pipeline;

    public string Id => SourceId;

    /// <summary>
    /// Never consulted, so not configurable: the manifest's timestamps are an Exact fingerprint,
    /// and Exact sources are imported when their version changes, however old their data.
    /// </summary>
    public RefreshPolicy RefreshPolicy => RefreshPolicy.Default;

    // CSV columns (0-indexed): id=0, lagebeztext=3, str=4, hnr=5, hnr_zus=6, plz=7, ort=8, gemeinde=9, flaeche_amtl=16
    public static readonly CsvColumnMap ColumnMap = new(
        Delimiter: ';',
        MinColumnCount: 17,
        StrIndex: 4,
        HnrIndex: 5,
        HnrZusIndex: 6,
        PlzIndex: 7,
        OrtIndex: 8,
        GemeindeIndex: 9,
        FlaecheAmtlIndex: 16);

    /// <summary>
    /// The newest ZIP in the manifest, fingerprinted by its name and publication timestamp — an
    /// Exact fingerprint, so an edition is downloaded once and never again. The manifest keeps
    /// every year's edition (all in one dataset today); only the newest is imported, since every
    /// import replaces all of NRW's rows. The newest GEBREF edition is part of the fingerprint
    /// too: a new one moves or adds positions, so it re-imports NRW as well. So is
    /// <see cref="Revision"/>.
    /// </summary>
    public async Task<SourceProbe> ProbeAsync(CancellationToken ct)
    {
        var grundsteuer = await NewestFileAsync(options.Value.ManifestUrl, ct);
        var fingerprint = $"{Revision}:{Stamp(grundsteuer)}";
        if (string.IsNullOrWhiteSpace(options.Value.GebrefManifestUrl))
            return new NrwProbe(grundsteuer.Name, fingerprint);

        var gebref = await NewestFileAsync(options.Value.GebrefManifestUrl, ct);
        return new NrwProbe(grundsteuer.Name, $"{fingerprint}|{Stamp(gebref)}", gebref.Name);
    }

    private async Task<ManifestFile> NewestFileAsync(string manifestUrl, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        var manifest = await Pipeline.ExecuteAsync(
            async token => await http.GetFromJsonAsync<GrundsteuerManifest>(manifestUrl, token), ct);

        return manifest?.Datasets.SelectMany(d => d.Files).MaxBy(f => f.Timestamp)
            ?? throw new InvalidDataException($"The NRW manifest at {manifestUrl} lists no files.");
    }

    private static string Stamp(ManifestFile file) =>
        $"{file.Name}@{file.Timestamp.ToString("s", CultureInfo.InvariantCulture)}";

    /// <summary>Downloads and parses exactly the ZIPs <paramref name="probe"/> found.</summary>
    public async IAsyncEnumerable<Property> FetchAsync(SourceProbe probe, ImportRunContext run, [EnumeratorCancellation] CancellationToken ct)
    {
        if (probe is not NrwProbe nrwProbe)
            throw new ArgumentException($"Expected the probe of {nameof(NrwPropertyImporter)}, got {probe.GetType().Name}.", nameof(probe));

        var http = httpClientFactory.CreateClient(HttpClientName);
        // Loaded before the (long) CSV, so a GEBREF that can't be read fails the import right away.
        var gebref = nrwProbe.GebrefFileName is { } gebrefFile ? await LoadGebrefAsync(http, gebrefFile, ct) : null;
        if (gebref is { Ambiguous: > 0 })
            await ResolvePostcodesAsync(gebref, ct);

        // The ZIP is about 1 GB – download it to a file first so ZipArchive can seek. The work
        // directory needs room for it.
        var zipPath = Path.Combine(_workDirectory, DownloadFileName);
        try
        {
            await DownloadAsync(http, $"{options.Value.BaseDownloadUrl.TrimEnd('/')}/{nrwProbe.FileName}", zipPath, ct);
            LogDownloadComplete(logger);

            long rows = 0;
            var located = new long[Enum.GetValues<LocatedBy>().Length];
            await using var zip = await ZipFile.OpenReadAsync(zipPath, ct);
            foreach (var entry in zip.Entries.Where(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
            {
                LogProcessingEntry(logger, entry.Name);
                await using var stream = await entry.OpenAsync(ct);
                using var reader = new StreamReader(stream);

                var headerSkipped = false;
                string? line;
                while ((line = await reader.ReadLineAsync(ct)) != null)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!headerSkipped) { headerSkipped = true; continue; }

                    var fields = DelimitedPropertyParser.SplitCsvLine(line, ColumnMap.Delimiter);
                    var property = DelimitedPropertyParser.ParseFields(fields, ColumnMap);
                    if (property is null) continue;

                    rows++;
                    if (gebref is not null)
                    {
                        var lagebezeichnung = DelimitedPropertyParser.Field(fields, LagebezeichnungIndex, ColumnMap.NullToken);
                        var (point, by) = Locate(gebref, property, lagebezeichnung);
                        located[(int)by]++;
                        if (point is { } p)
                        {
                            property.Lat = p.Lat;
                            property.Lon = p.Lon;
                        }
                    }
                    yield return property;
                }
            }

            if (gebref is not null)
                LogLocated(logger, rows, located, rows == 0 ? 1 : rows);
        }
        finally
        {
            if (File.Exists(zipPath))
                File.Delete(zipPath);
        }
    }

    /// <summary>How a row got its position, for the import's log.</summary>
    private enum LocatedBy { Address, AddressAndPostcode, Lagebezeichnung, None }

    /// <summary>
    /// The row's GEBREF position, by its own address first. A suffix of "uneindeutig" means the
    /// parcel carries several of the number's suffixes (17, 17a, …), so the number's own
    /// building is the closest the data gets. A street or number of "uneindeutig" (~183,000
    /// rows) names no address at all; their parcel's Lagebezeichnung lists the real ones, and
    /// the first GEBREF knows is a building on the parcel. It also places ~7,000 rows whose own
    /// address GEBREF spells differently or doesn't list.
    /// </summary>
    private static (GebrefMatch? Point, LocatedBy By) Locate(GebrefIndex gebref, Property property, string? lagebezeichnung)
    {
        var own = gebref.Find(property.Gemeinde, property.Str, property.Hnr, property.HnrZus, property.Plz)
            ?? (string.Equals(property.HnrZus, Unclear, StringComparison.OrdinalIgnoreCase)
                ? gebref.Find(property.Gemeinde, property.Str, property.Hnr, suffix: null, property.Plz)
                : null);
        if (own is { } match)
            return (match, match.ByPostcode ? LocatedBy.AddressAndPostcode : LocatedBy.Address);

        foreach (var address in LagebezeichnungParser.Parse(lagebezeichnung).Addresses)
        {
            if (gebref.Find(property.Gemeinde, address.Str, address.Hnr, address.HnrZus, property.Plz) is { } point)
                return (point, LocatedBy.Lagebezeichnung);
        }
        return (null, LocatedBy.None);
    }

    /// <summary>
    /// Loads the postcode areas over GEBREF's extent so the PLZ picks among an address's several
    /// points. Not worth failing the import for: without them those ~9,000 rows just stay
    /// without a position, as before.
    /// </summary>
    private async Task ResolvePostcodesAsync(GebrefIndex gebref, CancellationToken ct)
    {
        if (postcodeAreas is null) return;
        try
        {
            var postcodes = await postcodeAreas.LoadAsync(25832, gebref.Extent, ct);
            LogPostcodesResolved(logger, gebref.ResolvePostcodes(postcodes), gebref.Ambiguous);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogPostcodesFailed(logger, ex, gebref.Ambiguous);
        }
    }

    /// <summary>Downloads the GEBREF ZIP the probe found and reads it into a lookup, deleting the file again.</summary>
    private async Task<GebrefIndex> LoadGebrefAsync(HttpClient http, string fileName, CancellationToken ct)
    {
        var zipPath = Path.Combine(_workDirectory, GebrefDownloadFileName);
        try
        {
            await DownloadAsync(http, $"{options.Value.GebrefBaseDownloadUrl.TrimEnd('/')}/{fileName}", zipPath, ct);

            await using var zip = await ZipFile.OpenReadAsync(zipPath, ct);
            var entry = zip.Entries.SingleOrDefault(e => e.Name.Equals("gebref.txt", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"GEBREF: {fileName} has no gebref.txt.");
            await using var stream = await entry.OpenAsync(ct);
            var index = await GebrefIndex.LoadAsync(stream, ct);
            LogGebrefLoaded(logger, index.Count, index.Ambiguous, index.Skipped);
            return index;
        }
        finally
        {
            if (File.Exists(zipPath))
                File.Delete(zipPath);
        }
    }

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="path"/>, replacing a leftover from an
    /// interrupted import. The whole download is one attempt: there is no resume, so a retry
    /// starts over.
    /// </summary>
    private async Task DownloadAsync(HttpClient http, string url, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(_workDirectory);
        if (File.Exists(path))
        {
            LogDeletingLeftover(logger, path);
            File.Delete(path);
        }

        LogDownloading(logger, url);
        await Pipeline.ExecuteAsync(async token =>
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using var fs = File.Create(path);
            await response.Content.CopyToAsync(fs, token);
        }, ct);
    }

    /// <summary>
    /// Retry with exponential backoff and jitter around a per-attempt timeout. No circuit breaker:
    /// one manifest and one ZIP per run give no failure ratio to measure.
    /// </summary>
    private ResiliencePipeline Pipeline => _pipeline ??=
        new ResiliencePipelineBuilder { TimeProvider = _time, Name = "nrw" }
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = args => ValueTask.FromResult(TransientErrors.IsTransient(args.Outcome.Exception)),
                MaxRetryAttempts = options.Value.MaxAttempts - 1,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(options.Value.RetryBaseDelaySeconds),
                MaxDelay = TimeSpan.FromSeconds(options.Value.MaxRetryDelaySeconds),
                OnRetry = args =>
                {
                    LogRetrying(logger, args.Outcome.Exception!, args.AttemptNumber + 1, options.Value.MaxAttempts, args.RetryDelay);
                    return default;
                },
            })
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(options.Value.DownloadTimeoutSeconds),
            })
            .Build();

    [LoggerMessage(Level = LogLevel.Warning, Message = "nrw: request failed (attempt {Attempt} of {MaxAttempts}), retrying in {Delay}")]
    private static partial void LogRetrying(ILogger logger, Exception exception, int attempt, int maxAttempts, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "nrw: deleting {Path}, left behind by an interrupted import")]
    private static partial void LogDeletingLeftover(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Downloading {Url}...")]
    private static partial void LogDownloading(ILogger logger, string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Download complete. Starting import...")]
    private static partial void LogDownloadComplete(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Processing {Entry}...")]
    private static partial void LogProcessingEntry(ILogger logger, string entry);

    [LoggerMessage(Level = LogLevel.Information, Message = "nrw: GEBREF has {Addresses} addresses with one position and {Ambiguous} listed at several; {Skipped} lines skipped")]
    private static partial void LogGebrefLoaded(ILogger logger, int addresses, int ambiguous, int skipped);

    [LoggerMessage(Level = LogLevel.Information, Message = "nrw: the PLZ tells apart the positions of {Resolvable} of the {Ambiguous} addresses GEBREF lists at several")]
    private static partial void LogPostcodesResolved(ILogger logger, int resolvable, int ambiguous);

    [LoggerMessage(Level = LogLevel.Warning, Message = "nrw: postcode areas unavailable; the {Ambiguous} addresses GEBREF lists at several stay without a position")]
    private static partial void LogPostcodesFailed(ILogger logger, Exception exception, int ambiguous);

    private static void LogLocated(ILogger logger, long rows, long[] located, long divisor) =>
        LogLocatedSteps(logger, rows,
            located[(int)LocatedBy.Address], (double)located[(int)LocatedBy.Address] / divisor,
            located[(int)LocatedBy.AddressAndPostcode], (double)located[(int)LocatedBy.AddressAndPostcode] / divisor,
            located[(int)LocatedBy.Lagebezeichnung], (double)located[(int)LocatedBy.Lagebezeichnung] / divisor,
            located[(int)LocatedBy.None], (double)located[(int)LocatedBy.None] / divisor);

    [LoggerMessage(Level = LogLevel.Information, Message = "nrw: positions of {Rows} rows: {ByAddress} by address ({AddressRatio:P2}), {ByPostcode} by address and PLZ ({PostcodeRatio:P2}), {ByLagebezeichnung} by the parcel's Lagebezeichnung ({LagebezeichnungRatio:P2}), {None} without ({NoneRatio:P2})")]
    private static partial void LogLocatedSteps(ILogger logger, long rows, long byAddress, double addressRatio, long byPostcode, double postcodeRatio,
        long byLagebezeichnung, double lagebezeichnungRatio, long none, double noneRatio);
}

/// <summary>
/// NRW's probe result: carries the ZIPs' file names to <see cref="NrwPropertyImporter.FetchAsync"/>;
/// <paramref name="GebrefFileName"/> is null when GEBREF is switched off.
/// </summary>
public sealed record NrwProbe(string FileName, string Fingerprint, string? GebrefFileName = null)
    : SourceProbe(Fingerprint, FingerprintKind.Exact);

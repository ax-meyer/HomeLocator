using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services.Importers.Scheduling;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace Grundstuecksfinder.Services.Importers.Nrw;

/// <summary>
/// Imports NRW's open-data "Grundsteuer" dataset: a JSON manifest listing one ZIP of
/// semicolon-CSVs per yearly edition, each with its publication timestamp.
/// </summary>
public partial class NrwPropertyImporter(
    ILogger<NrwPropertyImporter> logger,
    IHttpClientFactory httpClientFactory,
    IOptions<NrwImporterOptions> options,
    TimeProvider? timeProvider = null) : IPropertySource
{
    public const string SourceId = "nrw";

    /// <summary>Named HttpClient for the manifest and the ZIP; per-attempt limits come from the options.</summary>
    public const string HttpClientName = "Nrw";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private ResiliencePipeline? _pipeline;

    public string Id => SourceId;

    /// <summary>
    /// Never consulted, so not configurable: the manifest's timestamps are an Exact fingerprint,
    /// and Exact sources are imported when their version changes, however old their data.
    /// </summary>
    public RefreshPolicy RefreshPolicy => RefreshPolicy.Default;

    // CSV columns (0-indexed): id=0, str=4, hnr=5, hnr_zus=6, plz=7, ort=8, gemeinde=9, flaeche_amtl=16
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
    /// import replaces all of NRW's rows.
    /// </summary>
    public async Task<SourceProbe> ProbeAsync(CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        var manifest = await Pipeline.ExecuteAsync(
            async token => await http.GetFromJsonAsync<GrundsteuerManifest>(options.Value.ManifestUrl, token), ct);

        var newest = manifest?.Datasets.SelectMany(d => d.Files).MaxBy(f => f.Timestamp)
            ?? throw new InvalidDataException($"The NRW manifest at {options.Value.ManifestUrl} lists no files.");
        var published = newest.Timestamp.ToString("s", CultureInfo.InvariantCulture);
        return new NrwProbe(newest.Name, $"{newest.Name}@{published}");
    }

    /// <summary>Downloads and parses exactly the ZIP <paramref name="probe"/> found.</summary>
    public async IAsyncEnumerable<Property> FetchAsync(SourceProbe probe, ImportRunContext run, [EnumeratorCancellation] CancellationToken ct)
    {
        if (probe is not NrwProbe nrwProbe)
            throw new ArgumentException($"Expected the probe of {nameof(NrwPropertyImporter)}, got {probe.GetType().Name}.", nameof(probe));

        var http = httpClientFactory.CreateClient(HttpClientName);
        var url = $"{options.Value.BaseDownloadUrl.TrimEnd('/')}/{nrwProbe.FileName}";

        // The ZIP can be several GB – download to a temp file first so ZipArchive can seek.
        // Ensure the host has at least 10 GB of free disk space.
        var tempFile = Path.Combine(Path.GetTempPath(), $"grundstuecksfinder_{Guid.NewGuid():N}.zip");
        try
        {
            LogDownloading(logger, url);
            // The whole download is one attempt: there is no resume, so a retry starts over.
            await Pipeline.ExecuteAsync(async token =>
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                await using var fs = File.Create(tempFile);
                await response.Content.CopyToAsync(fs, token);
            }, ct);

            LogDownloadComplete(logger);

            await using var zip = await ZipFile.OpenReadAsync(tempFile, ct);
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

                    var property = DelimitedPropertyParser.ParseLine(line, ColumnMap);
                    if (property != null) yield return property;
                }
            }
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Downloading {Url}...")]
    private static partial void LogDownloading(ILogger logger, string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Download complete. Starting import...")]
    private static partial void LogDownloadComplete(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Processing {Entry}...")]
    private static partial void LogProcessingEntry(ILogger logger, string entry);
}

/// <summary>NRW's probe result: carries the ZIP's file name to <see cref="NrwPropertyImporter.FetchAsync"/>.</summary>
public sealed record NrwProbe(string FileName, string Fingerprint) : SourceProbe(Fingerprint, FingerprintKind.Exact);

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Grundstuecksfinder.Services.Importers.Inspire.Addresses;

/// <summary>
/// Addresses from a statewide "Hauskoordinaten" text file (<see cref="AddressSourceType.HkFile"/>),
/// downloaded as a ZIP, read by <see cref="HkFileReader"/> and preloaded. For states whose
/// INSPIRE address WFS is the slow half of the import (BW: hours of tiles vs a 73 MB file) or
/// damaged (HE's WFS replaces every umlaut with U+FFFD; its file is clean UTF-8).
/// </summary>
/// <remarks>
/// The file's version comes from its publisher (see <see cref="IHkFileLocator"/>), so this part
/// of the fingerprint is exact. The ZIP goes to the work directory rather than memory — the
/// reader streams its member from there — under a fixed name per source, like NRW's: it is
/// deleted as soon as it has been read, whether or not that worked, and one left behind by a
/// killed process is found and deleted by the next import instead of piling up. Only one
/// import of a source runs at a time, so the name can't collide.
/// </remarks>
public sealed partial class HkFileAddressProvider(
    InspireServiceClient client,
    InspireSourceOptions options,
    IHkFileLocator locator,
    string workDirectory,
    ILogger logger) : IAddressProvider
{
    private const string What = "Hauskoordinaten file";

    private AddressSourceOptions Settings => options.AddressSource;

    /// <summary>The download's file name in the work directory.</summary>
    public static string DownloadFileName(string source) => $"{source}-hauskoordinaten.zip";

    public async Task<FingerprintPart> ProbeAsync(CancellationToken ct) =>
        new((await client.ExecuteAsync(locator.LocateAsync, What, Settings.Url, ct)).Version, IsExact: true);

    /// <remarks>
    /// The file is located afresh, and on every download attempt, rather than taken from what
    /// the probe found: the probe may have run hours earlier, and Hessen's download link only
    /// works on the day it was listed — a retry after midnight needs that day's link. Should
    /// the edition have changed since the probe, the newer one is imported rather than failing
    /// the run, a warning names both versions, and the loaded addresses carry the version
    /// actually imported (see <see cref="ITileAddresses.Loaded"/>).
    /// </remarks>
    public async Task<ITileAddresses> LoadAsync(FingerprintPart probed, CancellationToken ct)
    {
        Directory.CreateDirectory(workDirectory);
        var path = Path.Combine(workDirectory, DownloadFileName(options.Source));
        if (File.Exists(path))
        {
            LogDeletingLeftover(logger, options.Source, path);
            File.Delete(path);
        }
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var (file, bytes) = await client.ExecuteAsync(async token =>
            {
                var located = await locator.LocateAsync(token);
                LogDownloading(logger, options.Source, located.Url, located.Version);
                return (located, await client.DownloadOnceAsync(located.Url, path, token));
            }, What, Settings.Url, ct, TimeSpan.FromSeconds(Settings.DownloadTimeoutSeconds));
            LogDownloaded(logger, options.Source, bytes / (1024.0 * 1024.0), stopwatch.Elapsed);
            if (probed.Value != file.Version)
                LogEditionChanged(logger, options.Source, probed.Value, file.Version);

            var addresses = new PreloadedAddresses.Builder();
            // Parsing ~0.5 GB of text is synchronous CPU work; keep it off the caller's thread.
            var result = await Task.Run(() => Read(path, addresses, ct), ct);
            LogRead(logger, options.Source, result.Rows, result.Accepted, string.Join(",", Settings.Qualities),
                result.OtherQuality, result.Malformed, stopwatch.Elapsed);

            if (result.Accepted == 0)
                throw new InspireImportException(
                    $"{options.Source}: the Hauskoordinaten file has no addresses of quality {string.Join(", ", Settings.Qualities)}.");

            // Malformed rows count as expected: many of them must fail the completeness check.
            return addresses.Build(result.Accepted + result.Malformed, loaded: new FingerprintPart(file.Version, IsExact: true));
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogDeleteFailed(logger, ex, options.Source, path);
            }
        }
    }

    private HkReadResult Read(string path, PreloadedAddresses.Builder into, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = SelectMember(zip.Entries, Settings.Member);
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);
        return HkFileReader.Read(reader, Settings.Qualities, options.CrsEpsgCode!.Value - 25800, into, ct);
    }

    /// <summary>
    /// The one entry matching <paramref name="pattern"/>: a name, or a pattern with * and ? for
    /// a member whose name carries the edition (Hessen's "…-2026-01.txt"). None or several fail
    /// the import rather than guess.
    /// </summary>
    public static ZipArchiveEntry SelectMember(IEnumerable<ZipArchiveEntry> entries, string pattern)
    {
        var regex = new Regex(
            "^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var all = entries.ToList();
        var matches = all.Where(e => regex.IsMatch(e.FullName)).ToList();
        return matches.Count == 1
            ? matches[0]
            : throw new InspireImportException(
                $"{matches.Count} entries of the Hauskoordinaten ZIP match \"{pattern}\"; it holds {string.Join(", ", all.Select(e => e.FullName))}.");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source}: the Hauskoordinaten file changed since the check for new data (was {Probed}, now {Current}); importing the current one")]
    private static partial void LogEditionChanged(ILogger logger, string source, string probed, string current);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: downloading the Hauskoordinaten file {Url} (version {Version})")]
    private static partial void LogDownloading(ILogger logger, string source, string url, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: downloaded {SizeMb:F1} MB in {Elapsed}")]
    private static partial void LogDownloaded(ILogger logger, string source, double sizeMb, TimeSpan elapsed);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: read {Rows} Hauskoordinaten rows: {Accepted} of quality {Qualities} kept, {OtherQuality} of other qualities dropped, {Malformed} malformed ({Elapsed} since the download started)")]
    private static partial void LogRead(ILogger logger, string source, long rows, long accepted, string qualities, long otherQuality, long malformed, TimeSpan elapsed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source}: deleting {Path}, left behind by an earlier import")]
    private static partial void LogDeletingLeftover(ILogger logger, string source, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source}: couldn't delete the downloaded file {Path}")]
    private static partial void LogDeleteFailed(ILogger logger, Exception exception, string source, string path);
}

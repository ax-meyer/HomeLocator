namespace Grundstuecksfinder.Services.Importers.Nrw;

public class NrwImporterOptions
{
    /// <summary>
    /// False stops importing NRW and hides its already-imported rows, like an INSPIRE source's
    /// "Enabled" flag.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public string ManifestUrl { get; set; } = "https://www.opengeodata.nrw.de/produkte/infrastruktur_bauen_wohnen/grundsteuer/index.json";
    public string BaseDownloadUrl { get; set; } = "https://www.opengeodata.nrw.de/produkte/infrastruktur_bauen_wohnen/grundsteuer/";

    /// <summary>
    /// Limit for one attempt at the manifest or the ZIP, covering the whole body. Needed because
    /// HttpClient.Timeout stops counting once the response headers have arrived, so a server
    /// stalling mid-download would otherwise hang the import indefinitely. The ZIP is about 1 GB.
    /// </summary>
    public double DownloadTimeoutSeconds { get; set; } = 1800;

    /// <summary>
    /// Attempts per request. Low on purpose: there is no resume, so every retry downloads the
    /// whole ZIP again, and a failed import is retried on the next run anyway.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Wait before the first retry; doubles with jitter, capped at <see cref="MaxRetryDelaySeconds"/>.</summary>
    public double RetryBaseDelaySeconds { get; set; } = 10;

    /// <summary>Upper bound for one retry wait.</summary>
    public double MaxRetryDelaySeconds { get; set; } = 60;
}

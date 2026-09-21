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
}

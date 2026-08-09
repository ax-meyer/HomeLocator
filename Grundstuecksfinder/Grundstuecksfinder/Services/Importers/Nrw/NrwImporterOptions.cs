namespace Grundstuecksfinder.Services.Importers.Nrw;

public class NrwImporterOptions
{
    public string ManifestUrl { get; set; } = "https://www.opengeodata.nrw.de/produkte/infrastruktur_bauen_wohnen/grundsteuer/index.json";
    public string BaseDownloadUrl { get; set; } = "https://www.opengeodata.nrw.de/produkte/infrastruktur_bauen_wohnen/grundsteuer/";
}

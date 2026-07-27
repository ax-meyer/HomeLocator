namespace Grundstuecksfinder.Models;

public class ImportLog
{
    public int Id { get; set; }
    public long ImportedAt { get; set; }
    public string DatasetName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileTimestamp { get; set; } = string.Empty;
    public long RecordCount { get; set; }
}

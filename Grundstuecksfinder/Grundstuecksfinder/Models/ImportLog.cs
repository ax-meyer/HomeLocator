namespace Grundstuecksfinder.Models;

public class ImportLog
{
    public int Id { get; set; }
    public long ImportedAt { get; set; }
    public string Source { get; set; } = string.Empty;
    public string DatasetName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileTimestamp { get; set; } = string.Empty;
    public long RecordCount { get; set; }

    /// <summary>
    /// Unix ms when the import committed; null while running or after a failure. Only completed
    /// logs count as "already imported", so a failed version is retried on the next run.
    /// </summary>
    public long? CompletedAt { get; set; }
}

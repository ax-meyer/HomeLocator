namespace Grundstuecksfinder.Services.Importers;

/// <summary>
/// "Import:WorkDirectory": where sources put downloaded files while they read them (NRW's ZIP,
/// Hauskoordinaten files), deleting them afterwards. Configurable because these are gigabytes
/// and the system temp directory may sit on a small or memory-backed volume.
/// </summary>
public static class ImportWorkDirectory
{
    public const string ConfigKey = "Import:WorkDirectory";

    /// <summary>A directory of our own under the system temp directory.</summary>
    public static string Default { get; } = Path.Combine(Path.GetTempPath(), "grundstuecksfinder");

    /// <summary>The configured directory, or <see cref="Default"/> when none is set.</summary>
    public static string Resolve(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? Default : configured;
}

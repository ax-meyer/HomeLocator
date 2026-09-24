namespace Grundstuecksfinder.Services.Importers.Scheduling;

/// <summary>Which of the worker's runs this is; decides whether routine re-imports may run.</summary>
public enum ImportRunKind
{
    /// <summary>
    /// The run at startup: only sources without served data are imported. Routine re-imports
    /// wait for the night, so restarts during the day can't add to the nightly cap — each one
    /// would otherwise start another multi-hour import.
    /// </summary>
    Startup,

    /// <summary>The nightly run: first imports and up to the configured number of routine re-imports.</summary>
    Nightly,
}

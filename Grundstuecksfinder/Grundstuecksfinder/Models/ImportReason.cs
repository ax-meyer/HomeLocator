namespace Grundstuecksfinder.Models;

/// <summary>Why an import run was started; recorded on the run for the import history.</summary>
public enum ImportReason
{
    /// <summary>The source has no served data yet (never imported, or every attempt failed).</summary>
    Initial,

    /// <summary>The source's fingerprint differs from the served one (for Approximate sources: and the data is old enough).</summary>
    Changed,

    /// <summary>The served data reached its maximum age; for sources whose fingerprint can miss changes.</summary>
    MaxAge,

    /// <summary>Due again after the source's latest import attempt failed.</summary>
    Retry,
}

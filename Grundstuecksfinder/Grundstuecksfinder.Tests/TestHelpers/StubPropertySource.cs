using System.Runtime.CompilerServices;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services.Importers;
using Grundstuecksfinder.Services.Importers.Scheduling;

namespace Grundstuecksfinder.Tests.TestHelpers;

/// <summary>A source whose upstream the test changes between runs.</summary>
public sealed class StubPropertySource(string id, string fingerprint, FingerprintKind kind = FingerprintKind.Exact, string street = "Teststraße")
    : IPropertySource
{
    public string Id => id;
    public RefreshPolicy RefreshPolicy { get; init; } = RefreshPolicy.Default;
    public string Fingerprint { get; set; } = fingerprint;
    public string Street { get; set; } = street;
    public int RowCount { get; set; } = 1;
    public int? FailAfter { get; set; }
    public int SkippedParts { get; init; }
    public Exception? ProbeFailure { get; set; }
    public Action? DuringFetch { get; set; }

    /// <summary>Reported as what the fetch really imported, like a file located anew at download time.</summary>
    public string? ImportedFingerprint { get; set; }

    public int Probes { get; private set; }
    public int Fetches { get; private set; }
    public List<SourceProbe> FetchedProbes { get; } = [];

    public Task<SourceProbe> ProbeAsync(CancellationToken ct)
    {
        Probes++;
        return ProbeFailure is { } failure
            ? Task.FromException<SourceProbe>(failure)
            : Task.FromResult(new SourceProbe(Fingerprint, kind));
    }

    public async IAsyncEnumerable<Property> FetchAsync(SourceProbe probe, ImportRunContext run, [EnumeratorCancellation] CancellationToken ct)
    {
        Fetches++;
        FetchedProbes.Add(probe);
        DuringFetch?.Invoke();
        // Known once the file is located, before any row: also recorded if the fetch then fails.
        if (ImportedFingerprint is not null)
            run.ReportFingerprint(ImportedFingerprint);
        for (var i = 0; i < RowCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            if (i == FailAfter)
                throw new HttpRequestException("upstream failed midway");
            yield return new Property { Str = Street, Hnr = $"{i}", Plz = "00000", Gemeinde = "Testgemeinde", FlaecheAmtl = 100, Source = id };
        }
        run.AddSkippedParts(SkippedParts);
    }
}

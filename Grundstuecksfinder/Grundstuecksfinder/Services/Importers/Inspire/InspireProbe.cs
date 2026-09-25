using System.Globalization;

namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>
/// One side's contribution to an INSPIRE source's probe: a value that changes when that side's
/// data does, and whether it is the publisher's own version marker.
/// </summary>
/// <param name="Value">Compared for equality only; never parsed.</param>
/// <param name="IsExact">
/// True for a publisher's version marker (ETag, Last-Modified, an edition name): it changes
/// exactly when the data does. False for a value derived from a live service, like a WFS hit
/// count: a change only says "something moved", and equal counts don't prove equal data.
/// </param>
public sealed record FingerprintPart(string Value, bool IsExact)
{
    /// <summary>
    /// A live service's feature count. Without one there is nothing to check an import's
    /// completeness against, and zero features means there is nothing to import, so both fail
    /// the probe (and the source is skipped this run).
    /// </summary>
    public static FingerprintPart FromCount(long? count, string what)
    {
        if (count is null)
            throw new InspireImportException(
                $"the {what} service doesn't report a feature count (numberMatched), so an import's completeness can't be checked.");
        if (count == 0)
            throw new InspireImportException($"the {what} service reports zero features.");
        return new FingerprintPart(count.Value.ToString(CultureInfo.InvariantCulture), IsExact: false);
    }
}

/// <summary>
/// An INSPIRE source's probe: the parcel side's part and the address side's, kept apart so the
/// fetch can hand each provider what its own probe saw (a file provider checks whether its
/// edition changed in between). <see cref="Addresses"/> is null for a source whose parcels name
/// their own addresses (<see cref="Inspire.Addresses.AddressSourceType.ParcelLagebezeichnung"/>).
/// </summary>
/// <remarks>
/// The fingerprint is the parts' values joined by ':', parcels first. A composite is only as
/// precise as its weakest part: it is <see cref="FingerprintKind.Exact"/> only when both parts
/// are, so one WFS hit count makes the whole probe <see cref="FingerprintKind.Approximate"/>.
/// </remarks>
public sealed record InspireProbe(string Fingerprint, FingerprintKind Kind, FingerprintPart Parcels, FingerprintPart? Addresses)
    : SourceProbe(Fingerprint, Kind)
{
    public static InspireProbe Combine(FingerprintPart parcels, FingerprintPart addresses) =>
        new($"{parcels.Value}:{addresses.Value}",
            parcels.IsExact && addresses.IsExact ? FingerprintKind.Exact : FingerprintKind.Approximate,
            parcels, addresses);

    /// <summary>The probe of a source without an address side: the parcel part alone.</summary>
    public static InspireProbe ParcelsOnly(FingerprintPart parcels) =>
        new(parcels.Value, parcels.IsExact ? FingerprintKind.Exact : FingerprintKind.Approximate, parcels, null);
}

using NetTopologySuite.Geometries;

namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>An ad:Address feature, parsed but not yet joined to a parcel.</summary>
public sealed record AddressFeature(
    Point Location,
    string? Str,
    string? Hnr,
    string? HnrZus,
    string? Plz,
    string? Ort,
    string? Gemeinde);

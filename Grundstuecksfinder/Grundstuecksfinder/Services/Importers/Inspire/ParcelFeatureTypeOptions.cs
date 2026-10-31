namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>
/// "ParcelFeatureType" of an "Import:Inspire:Sources" entry: which feature type of the parcel
/// WFS to fetch, and which of its child elements hold what. The defaults are INSPIRE's
/// cp:CadastralParcel; states whose INSPIRE service is missing or worse publish the AdV
/// "ALKIS vereinfacht" schema instead (ave:Flurstueck: "flaeche", "gemeinde", and the parcel's
/// own addresses as text in "lagebeztxt").
/// </summary>
public class ParcelFeatureTypeOptions : FeatureTypeOptions
{
    /// <inheritdoc/>
    public override string TypeName { get; set; } = "cp:CadastralParcel";

    /// <summary>The child element holding the official area in m² ("areaValue", "flaeche").</summary>
    public string AreaField { get; set; } = "areaValue";

    /// <summary>The child element holding the Gemeinde's name; unset reads none.</summary>
    public string? GemeindeField { get; set; }

    /// <summary>
    /// The child element holding the parcel's Lagebezeichnungen as text, e.g. "Rheinstraße 105,
    /// 107; Klarastraße 2" (see <see cref="LagebezeichnungParser"/>); unset reads none.
    /// </summary>
    public string? LagebezeichnungField { get; set; }

    /// <inheritdoc/>
    public override IEnumerable<string> Validate()
    {
        foreach (var error in base.Validate())
            yield return error;
        if (string.IsNullOrWhiteSpace(AreaField))
            yield return "AreaField must name the element holding the area.";
        if (GemeindeField is not null && string.IsNullOrWhiteSpace(GemeindeField))
            yield return "GemeindeField must not be blank; leave it out to read none.";
        if (LagebezeichnungField is not null && string.IsNullOrWhiteSpace(LagebezeichnungField))
            yield return "LagebezeichnungField must not be blank; leave it out to read none.";
    }
}

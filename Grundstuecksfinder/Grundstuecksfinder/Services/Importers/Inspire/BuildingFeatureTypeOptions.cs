namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>
/// "BuildingFeatureType" of an "Import:Inspire:Sources" entry whose parcels name their own
/// addresses: the buildings of the same WFS, which name theirs too (ALKIS vereinfacht
/// ave:GebaeudeBauwerk, "lagebeztxt"). They give the addresses of a parcel with several
/// addresses a position each: the building that names the address.
/// </summary>
public class BuildingFeatureTypeOptions : FeatureTypeOptions
{
    /// <inheritdoc/>
    public override string TypeName { get; set; } = "ave:GebaeudeBauwerk";

    /// <summary>The child element holding the building's addresses as text, like a parcel's.</summary>
    public string LagebezeichnungField { get; set; } = "lagebeztxt";

    /// <inheritdoc/>
    public override IEnumerable<string> Validate()
    {
        foreach (var error in base.Validate())
            yield return error;
        if (string.IsNullOrWhiteSpace(LagebezeichnungField))
            yield return "LagebezeichnungField must name the element holding the building's addresses.";
    }
}

namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>
/// "ParcelFeatureType" of an "Import:Inspire:Sources" entry: which feature type of the parcel
/// WFS to fetch, and which of its child elements hold what. The defaults are INSPIRE's
/// cp:CadastralParcel; states whose INSPIRE service is missing or worse publish the AdV
/// "ALKIS vereinfacht" schema instead (ave:Flurstueck: "flaeche", "gemeinde", and the parcel's
/// own addresses as text in "lagebeztxt").
/// </summary>
public class ParcelFeatureTypeOptions
{
    /// <summary>The feature type as the WFS names it, with its prefix, e.g. "ave:Flurstueck".</summary>
    public string TypeName { get; set; } = "cp:CadastralParcel";

    /// <summary>
    /// The namespace URI of <see cref="TypeName"/>'s prefix, sent as the WFS NAMESPACES
    /// parameter; only for servers that don't resolve the prefix on their own (Thüringen answers
    /// "Unable to determine targeted feature type" without it). Unset sends none.
    /// </summary>
    public string? Namespace { get; set; }

    /// <summary>The child element holding the official area in m² ("areaValue", "flaeche").</summary>
    public string AreaField { get; set; } = "areaValue";

    /// <summary>The child element holding the Gemeinde's name; unset reads none.</summary>
    public string? GemeindeField { get; set; }

    /// <summary>
    /// The child element holding the parcel's Lagebezeichnungen as text, e.g. "Rheinstraße 105,
    /// 107; Klarastraße 2" (see <see cref="LagebezeichnungParser"/>); unset reads none.
    /// </summary>
    public string? LagebezeichnungField { get; set; }

    /// <summary><see cref="TypeName"/> without its prefix: the element name a feature is matched by.</summary>
    public string LocalName => TypeName[(TypeName.IndexOf(':', StringComparison.Ordinal) + 1)..];

    /// <summary><see cref="TypeName"/>'s prefix, or null if it has none.</summary>
    public string? Prefix
    {
        get
        {
            var colon = TypeName.IndexOf(':', StringComparison.Ordinal);
            return colon > 0 ? TypeName[..colon] : null;
        }
    }

    /// <summary>One message per problem, each starting with the offending property's name.</summary>
    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(TypeName) || TypeName.Any(char.IsWhiteSpace) || string.IsNullOrEmpty(LocalName))
            yield return "TypeName must name a feature type, like \"cp:CadastralParcel\".";
        if (Namespace is not null)
        {
            if (!Uri.TryCreate(Namespace, UriKind.Absolute, out _))
                yield return "Namespace must be an absolute URI.";
            if (Prefix is null)
                yield return "Namespace needs a TypeName with a prefix to bind it to.";
        }
        if (string.IsNullOrWhiteSpace(AreaField))
            yield return "AreaField must name the element holding the area.";
        if (GemeindeField is not null && string.IsNullOrWhiteSpace(GemeindeField))
            yield return "GemeindeField must not be blank; leave it out to read none.";
        if (LagebezeichnungField is not null && string.IsNullOrWhiteSpace(LagebezeichnungField))
            yield return "LagebezeichnungField must not be blank; leave it out to read none.";
    }
}

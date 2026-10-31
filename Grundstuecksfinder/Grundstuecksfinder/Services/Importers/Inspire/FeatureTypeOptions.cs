namespace Grundstuecksfinder.Services.Importers.Inspire;

/// <summary>
/// The part every configured WFS feature type shares (<see cref="ParcelFeatureTypeOptions"/>,
/// <see cref="BuildingFeatureTypeOptions"/>): its name and the namespace its prefix is bound to.
/// </summary>
public abstract class FeatureTypeOptions
{
    /// <summary>The feature type as the WFS names it, with its prefix, e.g. "ave:Flurstueck".</summary>
    public abstract string TypeName { get; set; }

    /// <summary>
    /// The namespace URI of <see cref="TypeName"/>'s prefix, sent as the WFS NAMESPACES
    /// parameter; only for servers that don't resolve the prefix on their own (Thüringen answers
    /// "Unable to determine targeted feature type" without it). Unset sends none.
    /// </summary>
    public string? Namespace { get; set; }

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
    public virtual IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(TypeName) || TypeName.Any(char.IsWhiteSpace) || string.IsNullOrEmpty(LocalName))
            yield return "TypeName must name a feature type, like \"ave:Flurstueck\".";
        if (Namespace is not null)
        {
            if (!Uri.TryCreate(Namespace, UriKind.Absolute, out _))
                yield return "Namespace must be an absolute URI.";
            if (Prefix is null)
                yield return "Namespace needs a TypeName with a prefix to bind it to.";
        }
    }
}

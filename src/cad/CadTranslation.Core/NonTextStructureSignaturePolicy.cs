namespace CadTranslation.Core;

/// <summary>
/// Selects structure-only geometry for non-text entities. A block reference's
/// geometric extents include the rendered extents of text inside its definition,
/// so those extents are not a stable non-text structure signal after translation.
/// </summary>
public static class NonTextStructureSignaturePolicy
{
    public static string GeometryToken(string objectType, string stablePlacement, string geometricExtents)
    {
        ArgumentNullException.ThrowIfNull(objectType);
        ArgumentNullException.ThrowIfNull(stablePlacement);
        ArgumentNullException.ThrowIfNull(geometricExtents);

        return string.Equals(objectType, "AcDbBlockReference", StringComparison.Ordinal)
            ? stablePlacement
            : geometricExtents;
    }
}

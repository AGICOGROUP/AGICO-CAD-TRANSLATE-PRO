using System.Globalization;

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
            : NormalizeExtents(geometricExtents);
    }

    private static string NormalizeExtents(string value)
    {
        string[] parts = value.Split(',');
        if (parts.Length != 6 || parts.Any(part => !double.TryParse(
                part,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out _)))
        {
            return value;
        }

        return string.Join(",", parts.Select(part =>
            double.Parse(part, CultureInfo.InvariantCulture).ToString("G15", CultureInfo.InvariantCulture)));
    }
}

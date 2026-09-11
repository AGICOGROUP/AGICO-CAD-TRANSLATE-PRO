using System.Globalization;
using System.Text.RegularExpressions;

namespace CadTranslation.Core;

/// <summary>
/// Selects structure-only geometry for non-text entities. A block reference's
/// geometric extents include the rendered extents of text inside its definition,
/// so those extents are not a stable non-text structure signal after translation.
/// </summary>
public static class NonTextStructureSignaturePolicy
{
    public static string StableOwnerPath(string ownerPath)
    {
        ArgumentNullException.ThrowIfNull(ownerPath);
        return Regex.Replace(ownerPath,
            @"/\*[A-Z][0-9A-F]+/([0-9A-F]+)(?=/|$)",
            "/<anonymous-block>/$1",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static string StableEntityIdentity(string handle, string ownerPath)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(ownerPath);
        // Legacy SaveAs may renumber entities inside anonymous (*X/*D/etc.)
        // definitions. Their type, owner, properties and geometry remain the
        // structural identity; the transient handle does not.
        return ownerPath.Contains("/<anonymous-block>", StringComparison.Ordinal)
            ? "<anonymous-entity>"
            : handle;
    }

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

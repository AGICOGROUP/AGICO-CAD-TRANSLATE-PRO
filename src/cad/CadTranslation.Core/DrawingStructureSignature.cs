using CadTranslation.Contracts;

namespace CadTranslation.Core;

public static class DrawingStructureSignature
{
    public static string ComputeRows(IEnumerable<string> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return Hashing.Sha256Text(string.Join("\n", rows.OrderBy(row => row, StringComparer.Ordinal)));
    }

    public static IReadOnlyList<string> TextRows(IEnumerable<TextStructureSignature> textEntities)
    {
        ArgumentNullException.ThrowIfNull(textEntities);
        return textEntities.Select(value => string.Join("|", "text", value.Handle, value.ObjectType,
            value.OwnerPath, value.Layer, value.Color, value.LineWeight, value.Properties))
            .OrderBy(row => row, StringComparer.Ordinal).ToArray();
    }

    public static string Compute(IEnumerable<string> nonTextRows, IEnumerable<TextStructureSignature> textEntities)
    {
        ArgumentNullException.ThrowIfNull(nonTextRows);
        ArgumentNullException.ThrowIfNull(textEntities);
        return ComputeRows(nonTextRows.Concat(TextRows(textEntities)));
    }
}

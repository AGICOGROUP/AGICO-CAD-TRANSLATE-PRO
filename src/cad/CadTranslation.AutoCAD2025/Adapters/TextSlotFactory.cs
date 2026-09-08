using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadTranslation.Contracts;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025.Adapters;

internal static class TextSlotFactory
{
    internal static TextSlot FromDbText(DBText value, string slot, string role) => new(
        slot, role, StableDbText(value), Geometry(value.Position, value.AlignmentPoint, value.Rotation),
        Properties(value, value.Height, value.WidthFactor));

    private static string StableDbText(DBText value)
    {
        if (!value.HasFields) return value.TextString;
        var field = (Field)value.Database.TransactionManager.TopTransaction.GetObject(value.GetField(), OpenMode.ForRead);
        string code = field.GetFieldCode();
        return code.StartsWith("%<", StringComparison.Ordinal) ? code : "%<" + code + ">%";
    }

    internal static TextSlot FromMText(MText value, string slot, string role) => new(
        slot, role, value.Contents, Geometry(value.Location, null, value.Rotation),
        Properties(value, value.TextHeight, 1.0, new Dictionary<string, string> { ["width"] = value.Width.ToString(System.Globalization.CultureInfo.InvariantCulture) }));

    internal static TextSlot FromDimension(Dimension value) => new(
        "override", "dimension-override", value.DimensionText, Geometry(Point3d.Origin, null, 0),
        Properties(value, 0, 1.0));

    internal static bool HasTranslatableLanguage(string value) => value.Any(character => character >= '\u4e00' && character <= '\u9fff');

    private static TextGeometry Geometry(Point3d point, Point3d? alignment, double rotation) => new(
        new Point3Snapshot(point.X, point.Y, point.Z),
        alignment is null ? null : new Point3Snapshot(alignment.Value.X, alignment.Value.Y, alignment.Value.Z), rotation, null);

    private static TextProperties Properties(Entity value, double height, double width, IReadOnlyDictionary<string, string>? specific = null) => new(
        value.Layer, value is DBText text ? text.TextStyleName : string.Empty, height, width,
        value is DBText dbText ? dbText.HorizontalMode.ToString() : string.Empty,
        value is DBText dbText2 ? dbText2.VerticalMode.ToString() : string.Empty,
        specific ?? new Dictionary<string, string>());
}

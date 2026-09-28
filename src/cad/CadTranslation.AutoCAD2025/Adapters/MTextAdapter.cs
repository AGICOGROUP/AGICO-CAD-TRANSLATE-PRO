using Autodesk.AutoCAD.DatabaseServices;

namespace CadTranslation.AutoCAD2025.Adapters;

internal sealed class MTextAdapter : ITextAdapter
{
    public bool CanHandle(DBObject value) => value is MText;
    public IEnumerable<TextSlot> Read(DBObject value, AdapterContext context)
    {
        if (value is MText text)
        {
            DrawingExporter.Progress("mtxt-loc " + text.Handle);
            var location = text.Location;
            DrawingExporter.Progress("mtxt-rot " + text.Handle);
            var rotation = text.Rotation;
            DrawingExporter.Progress("mtxt-height " + text.Handle);
            var height = text.TextHeight;
            DrawingExporter.Progress("mtxt-width " + text.Handle);
            var width = text.Width;
            DrawingExporter.Progress("mtxt-layer " + text.Handle);
            var layer = text.Layer;
            DrawingExporter.Progress("mtxt-contents-before " + text.Handle);
            var contents = text.Contents;
            DrawingExporter.Progress("mtxt-contents-after " + text.Handle);
            if (!string.IsNullOrWhiteSpace(contents))
                yield return new TextSlot("contents", "text", contents,
                    TextSlotFactory.Geometry(location, null, rotation),
                    TextSlotFactory.Properties(text, height, 1.0,
                        new Dictionary<string, string> { ["width"] = width.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        }
    }
    public bool CanWriteSlot(DBObject value, string slot) => value is MText && slot == "contents";
    public void Write(DBObject value, string slot, string restoredText)
    {
        if (!CanWriteSlot(value, slot)) throw new InvalidOperationException("MText slot does not match manifest.");
        ((MText)value).Contents = restoredText;
    }
}

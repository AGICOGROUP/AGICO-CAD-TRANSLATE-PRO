using Autodesk.AutoCAD.DatabaseServices;

namespace CadTranslation.AutoCAD2025.Adapters;

internal sealed class DbTextAdapter : ITextAdapter
{
    public bool CanHandle(DBObject value) => value is DBText and not AttributeDefinition and not AttributeReference;
    public IEnumerable<TextSlot> Read(DBObject value, AdapterContext context)
    {
        if (value is DBText text && !string.IsNullOrWhiteSpace(text.TextString)) yield return TextSlotFactory.FromDbText(text, "text", "text");
    }
    public bool CanWriteSlot(DBObject value, string slot) => value is DBText and not AttributeDefinition and not AttributeReference && slot == "text";
    public void Write(DBObject value, string slot, string restoredText)
    {
        if (!CanWriteSlot(value, slot)) throw new InvalidOperationException("DBText slot does not match manifest.");
        var text = (DBText)value;
        text.TextString = restoredText;
        // Keep the original alignment anchor after the string (and thus its extents)
        // changes; a no-op for left/base-left text.
        text.AdjustAlignment(text.Database);
    }
}

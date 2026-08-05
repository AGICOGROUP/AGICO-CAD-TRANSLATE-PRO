using Autodesk.AutoCAD.DatabaseServices;

namespace CadTranslation.AutoCAD2025.Adapters;

internal sealed class MTextAdapter : ITextAdapter
{
    public bool CanHandle(DBObject value) => value is MText;
    public IEnumerable<TextSlot> Read(DBObject value, AdapterContext context)
    {
        if (value is MText text && !string.IsNullOrWhiteSpace(text.Contents)) yield return TextSlotFactory.FromMText(text, "contents", "text");
    }
    public bool CanWriteSlot(DBObject value, string slot) => value is MText && slot == "contents";
    public void Write(DBObject value, string slot, string restoredText)
    {
        if (!CanWriteSlot(value, slot)) throw new InvalidOperationException("MText slot does not match manifest.");
        ((MText)value).Contents = restoredText;
    }
}

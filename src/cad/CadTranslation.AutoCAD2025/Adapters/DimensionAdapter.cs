using Autodesk.AutoCAD.DatabaseServices;

namespace CadTranslation.AutoCAD2025.Adapters;

internal sealed class DimensionAdapter : ITextAdapter
{
    public bool CanHandle(DBObject value) => value is Dimension;
    public IEnumerable<TextSlot> Read(DBObject value, AdapterContext context)
    {
        // Reading is language-neutral: the verifier must still see an override
        // after its Chinese text (or Chinese font name) has been translated.
        if (value is Dimension dimension && !string.IsNullOrWhiteSpace(dimension.DimensionText))
            yield return TextSlotFactory.FromDimension(dimension);
    }
    public bool CanWriteSlot(DBObject value, string slot) => value is Dimension && slot == "override";
    public void Write(DBObject value, string slot, string restoredText)
    {
        if (!CanWriteSlot(value, slot)) throw new InvalidOperationException("Dimension slot does not match manifest.");
        ((Dimension)value).DimensionText = restoredText;
    }
}

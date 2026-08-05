using Autodesk.AutoCAD.DatabaseServices;

namespace CadTranslation.AutoCAD2025.Adapters;

internal sealed class AttributeAdapter : ITextAdapter
{
    public bool CanHandle(DBObject value) => value is AttributeDefinition or AttributeReference;
    public IEnumerable<TextSlot> Read(DBObject value, AdapterContext context)
    {
        switch (value)
        {
            case AttributeDefinition definition when !string.IsNullOrWhiteSpace(definition.TextString):
                yield return TextSlotFactory.FromDbText(definition, $"tag:{definition.Tag}", "attribute-definition");
                break;
            case AttributeReference reference when !string.IsNullOrWhiteSpace(reference.TextString):
                yield return TextSlotFactory.FromDbText(reference, $"tag:{reference.Tag}", "attribute-reference");
                break;
        }
    }
    public bool CanWriteSlot(DBObject value, string slot) => value switch
    {
        AttributeDefinition definition => slot == $"tag:{definition.Tag}",
        AttributeReference reference => slot == $"tag:{reference.Tag}",
        _ => false
    };
    public void Write(DBObject value, string slot, string restoredText)
    {
        if (!CanWriteSlot(value, slot)) throw new InvalidOperationException("Attribute slot does not match manifest.");
        switch (value)
        {
            case AttributeDefinition definition: definition.TextString = restoredText; break;
            case AttributeReference reference: reference.TextString = restoredText; break;
            default: throw new InvalidOperationException("Attribute slot does not match manifest.");
        }
    }
}

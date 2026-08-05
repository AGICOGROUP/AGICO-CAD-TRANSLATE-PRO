using Autodesk.AutoCAD.DatabaseServices;

namespace CadTranslation.AutoCAD2025.Adapters;

internal interface ITextAdapter
{
    bool CanHandle(DBObject value);
    IEnumerable<TextSlot> Read(DBObject value, AdapterContext context);
    bool CanWriteSlot(DBObject value, string slot);
    void Write(DBObject value, string slot, string restoredText);
}

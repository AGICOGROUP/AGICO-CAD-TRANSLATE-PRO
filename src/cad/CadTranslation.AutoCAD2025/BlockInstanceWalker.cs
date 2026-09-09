using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

internal static class BlockInstanceWalker
{
    internal static BlockInstancePath[] Capture(Database database, Transaction transaction)
    {
        BlockTable blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        var definitions = new Dictionary<string, BlockDefinitionNode>(StringComparer.Ordinal);
        var roots = new HashSet<string>(StringComparer.Ordinal);

        foreach (ObjectId blockId in blockTable)
        {
            var block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
            if (block.IsFromExternalReference || block.IsFromOverlayReference)
            {
                continue;
            }

            var references = new List<BlockReferenceNode>();
            foreach (ObjectId entityId in block)
            {
                if (transaction.GetObject(entityId, OpenMode.ForRead, false) is not BlockReference reference)
                {
                    continue;
                }

                // Legacy/proxy objects may expose an unresolved block reference.
                // It has no usable topology and must not abort translation of the drawing.
                if (reference.BlockTableRecord.IsNull)
                {
                    continue;
                }

                var target = (BlockTableRecord)transaction.GetObject(reference.BlockTableRecord, OpenMode.ForRead);
                if (target.IsFromExternalReference || target.IsFromOverlayReference)
                {
                    continue;
                }

                references.Add(new BlockReferenceNode(
                    reference.Handle.ToString(),
                    target.Name,
                    ToTransform2(reference.BlockTransform)));
            }

            definitions[block.Name] = new BlockDefinitionNode(block.Name, references);
        }

        DBDictionary layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry entry in layouts)
        {
            var layout = (Layout)transaction.GetObject(entry.Value, OpenMode.ForRead);
            if (layout.BlockTableRecordId.IsNull)
            {
                continue;
            }
            var root = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            if (definitions.ContainsKey(root.Name))
            {
                roots.Add(root.Name);
            }
        }

        return roots
            .OrderBy(root => root, StringComparer.Ordinal)
            .SelectMany(root => BlockInstanceExpander.Expand(root, definitions))
            .ToArray();
    }

    private static Transform2 ToTransform2(Matrix3d matrix) => new(
        matrix[0, 0],
        matrix[0, 1],
        matrix[1, 0],
        matrix[1, 1],
        matrix[0, 3],
        matrix[1, 3]);
}

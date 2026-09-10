using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

internal static class BlockInstanceWalker
{
    internal static BlockInstancePath[] Capture(Database database, Transaction transaction, CadObjectAccess? access = null)
    {
        access ??= new CadObjectAccess(database, transaction);
        BlockTable blockTable = access.Read<BlockTable>(database.BlockTableId, "instances-block-table", required: true)!;
        var definitions = new Dictionary<string, BlockDefinitionNode>(StringComparer.Ordinal);
        var roots = new HashSet<string>(StringComparer.Ordinal);

        foreach (ObjectId blockId in blockTable)
        {
            var block = access.Read<BlockTableRecord>(blockId, "instances-block", parentHandle: blockTable.Handle.ToString());
            if (block is null) continue;
            if (block.IsFromExternalReference || block.IsFromOverlayReference)
            {
                continue;
            }

            var references = new List<BlockReferenceNode>();
            foreach (ObjectId entityId in block)
            {
                if (access.Read<Entity>(entityId, "instances-member", block.Name, block.Handle.ToString()) is not BlockReference reference)
                {
                    continue;
                }

                var target = access.Read<BlockTableRecord>(reference.BlockTableRecord, "instances-target", block.Name, reference.Handle.ToString());
                if (target is null) continue;
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

        DBDictionary layouts = access.Read<DBDictionary>(database.LayoutDictionaryId, "instances-layouts", required: true)!;
        foreach (DBDictionaryEntry entry in layouts)
        {
            var layout = access.Read<Layout>(entry.Value, "instances-layout", parentHandle: layouts.Handle.ToString());
            if (layout is null) continue;
            var root = access.Read<BlockTableRecord>(layout.BlockTableRecordId, "instances-layout-root", parentHandle: layout.Handle.ToString());
            if (root is null) continue;
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

using Autodesk.AutoCAD.DatabaseServices;

namespace CadTranslation.AutoCAD2025;

internal sealed record WalkItem(DBObject Value, string OwnerPath, string Handle, bool IsDimensionGeneratedDisplay);

internal static class EntityWalker
{
    internal static IEnumerable<WalkItem> Walk(Database database, Transaction transaction)
    {
        BlockTable blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        var dimensionBlockIds = FindDimensionGeneratedBlocks(blockTable, transaction);
        var visited = new HashSet<ObjectId>();

        foreach (ObjectId blockId in blockTable)
        {
            var block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
            if (block.IsAnonymous && dimensionBlockIds.Contains(blockId))
            {
                continue;
            }

            string blockOwnerPath = $"ROOT/BLOCK/{block.Name}/{block.Handle}";
            foreach (ObjectId entityId in block)
            {
                if (!visited.Add(entityId))
                {
                    continue;
                }

                var value = transaction.GetObject(entityId, OpenMode.ForRead, false);
                yield return new WalkItem(value, blockOwnerPath, value.Handle.ToString(), false);

                if (value is not BlockReference reference)
                {
                    continue;
                }

                foreach (ObjectId attributeId in reference.AttributeCollection)
                {
                    if (!visited.Add(attributeId))
                    {
                        continue;
                    }

                    var attribute = (AttributeReference)transaction.GetObject(attributeId, OpenMode.ForRead, false);
                    string ownerPath = $"{blockOwnerPath}/BLOCKREF/{reference.Handle}/ATTRIB/{attribute.Tag}";
                    yield return new WalkItem(attribute, ownerPath, attribute.Handle.ToString(), false);
                }
            }
        }
    }

    private static HashSet<ObjectId> FindDimensionGeneratedBlocks(BlockTable blockTable, Transaction transaction)
    {
        var result = new HashSet<ObjectId>();
        foreach (ObjectId blockId in blockTable)
        {
            var block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
            foreach (ObjectId entityId in block)
            {
                if (transaction.GetObject(entityId, OpenMode.ForRead, false) is Dimension dimension && !dimension.DimBlockId.IsNull)
                {
                    result.Add(dimension.DimBlockId);
                }
            }
        }
        return result;
    }
}

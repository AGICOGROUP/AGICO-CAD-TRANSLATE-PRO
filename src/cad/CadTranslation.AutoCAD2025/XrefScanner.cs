using System.Security.Cryptography;
using Autodesk.AutoCAD.DatabaseServices;

namespace CadTranslation.AutoCAD2025;

internal static class XrefScanner
{
    internal static IReadOnlyList<XrefSnapshot> Scan(Database database, Transaction transaction, string drawingPath)
    {
        // This API includes nested references; the block records below provide the stored path
        // and status for each concrete reference in the current database.
        XrefGraph graph = database.GetHostDwgXrefGraph(true);
        int graphNodeCount = graph.NumNodes;
        var graphNodesByBlock = new Dictionary<ObjectId, XrefGraphNode>();
        for (int index = 0; index < graphNodeCount; index++)
        {
            XrefGraphNode node = graph.GetXrefNode(index);
            if (!node.BlockTableRecordId.IsNull)
            {
                graphNodesByBlock[node.BlockTableRecordId] = node;
            }
        }
        BlockTable blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        var xrefs = new List<XrefSnapshot>();

        foreach (ObjectId blockId in blockTable)
        {
            var block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
            if (!block.IsFromExternalReference)
            {
                continue;
            }

            string storedPath = block.PathName ?? string.Empty;
            string canonicalPath = ResolvePath(drawingPath, storedPath);
            FileSnapshot file = InspectFile(canonicalPath);
            string status = block.XrefStatus.ToString();
            bool overlay = block.IsFromOverlayReference;
            xrefs.Add(new XrefSnapshot(
                block.Name,
                canonicalPath,
                storedPath,
                graphNodesByBlock.TryGetValue(blockId, out XrefGraphNode? node)
                    ? ParentChain(node)
                    : Array.Empty<string>(),
                status,
                overlay,
                !overlay,
                file.Writable,
                file.Sha256,
                graphNodeCount));
        }

        return xrefs.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<string> ParentChain(XrefGraphNode node)
    {
        var chain = new List<string>();
        var seen = new HashSet<IntPtr>();
        AddParents(node, chain, seen);
        return chain;
    }

    private static void AddParents(XrefGraphNode node, ICollection<string> chain, ISet<IntPtr> seen)
    {
        if (!seen.Add(node.UnmanagedObject))
        {
            return;
        }
        for (int index = 0; index < node.NumIn; index++)
        {
            if (node.In(index) is not XrefGraphNode parent)
            {
                continue;
            }
            AddParents(parent, chain, seen);
            chain.Add(parent.Name);
        }
    }

    private static string ResolvePath(string drawingPath, string storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
        {
            return string.Empty;
        }
        return Path.GetFullPath(Path.IsPathFullyQualified(storedPath)
            ? storedPath
            : Path.Combine(Path.GetDirectoryName(drawingPath) ?? string.Empty, storedPath));
    }

    private static FileSnapshot InspectFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new FileSnapshot(false, null);
        }

        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return new FileSnapshot(true, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
        }
        catch (UnauthorizedAccessException)
        {
            return new FileSnapshot(false, null);
        }
        catch (IOException)
        {
            return new FileSnapshot(false, null);
        }
    }

    private sealed record FileSnapshot(bool Writable, string? Sha256);
}

internal sealed record XrefSnapshot(
    string Name,
    string CanonicalPath,
    string StoredPath,
    IReadOnlyList<string> ParentChain,
    string Status,
    bool Overlay,
    bool Attached,
    bool Writable,
    string? Sha256,
    int GraphNodeCount);

using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using CadTranslation.Contracts;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

internal static class DrawingScanner
{
    internal static int Scan(JobContext context)
    {
        context.VerifySourceAndWorkingHashes();
        Database database = HostApplicationServices.WorkingDatabase;
        string sourceHashBefore = Hashing.Sha256File(context.Config.SourcePath);
        using Transaction transaction = database.TransactionManager.StartTransaction();
        ScanSnapshot snapshot = BuildSnapshot(database, transaction, context.Config.SourcePath, sourceHashBefore);
        transaction.Commit();
        context.VerifySourceAndWorkingHashes();
        string sourceHashAfter = Hashing.Sha256File(context.Config.SourcePath);
        snapshot = snapshot with { SourceSha256After = sourceHashAfter };
        string scanPath = Path.Combine(context.Config.ArtifactDirectory, "scan.json");
        AtomicFile.WriteUtf8(scanPath, JsonSerializer.Serialize(snapshot, JsonDefaults.Options));
        return snapshot.Counts.Values.Sum();
    }

    private static ScanSnapshot BuildSnapshot(Database database, Transaction transaction, string sourcePath, string sourceHashBefore)
    {
        BlockTable blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal)
        {
            ["BlockDefinitions"] = 0,
            ["AnonymousBlocks"] = 0,
            ["TEXT"] = 0,
            ["MTEXT"] = 0,
            ["ATTRIB"] = 0,
            ["ATTDEF"] = 0,
            ["DIMENSION"] = 0
        };
        foreach (ObjectId blockId in blockTable)
        {
            var block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
            if (!block.IsLayout)
            {
                counts["BlockDefinitions"]++;
            }
            if (block.IsAnonymous)
            {
                counts["AnonymousBlocks"]++;
            }
        }

        var proxies = new List<ProxySnapshot>();
        foreach (WalkItem item in EntityWalker.Walk(database, transaction))
        {
            switch (item.Value)
            {
                case AttributeReference:
                    counts["ATTRIB"]++;
                    break;
                case AttributeDefinition:
                    counts["ATTDEF"]++;
                    break;
                case DBText:
                    counts["TEXT"]++;
                    break;
                case MText:
                    counts["MTEXT"]++;
                    break;
                case Dimension:
                    counts["DIMENSION"]++;
                    // MVP inventory keeps one semantic text slot for each dimension while
                    // dimension-generated anonymous display blocks remain excluded.
                    counts["MTEXT"]++;
                    break;
            }
            string className = item.Value.GetRXClass().Name;
            if (className.Contains("Proxy", StringComparison.OrdinalIgnoreCase))
            {
                proxies.Add(new ProxySnapshot(item.Handle, className, false));
            }
        }

        IReadOnlyList<FontSnapshot> fonts = ScanFonts(database, transaction);
        IReadOnlyList<XrefSnapshot> xrefs = XrefScanner.Scan(database, transaction, sourcePath);
        IReadOnlyList<LayoutSnapshot> layouts = ScanLayouts(database, transaction);
        var blockers = new SortedSet<string>(StringComparer.Ordinal);
        if (fonts.Any(font => !font.Exists)) blockers.Add("missing_font");
        if (xrefs.Any(xref => string.Equals(xref.Status, "Unresolved", StringComparison.OrdinalIgnoreCase))) blockers.Add("missing_xref");
        if (xrefs.Any(xref => !string.IsNullOrEmpty(xref.CanonicalPath) && xref.Sha256 is null)) blockers.Add("inaccessible_xref");
        if (proxies.Any(proxy => proxy.HasCjkText)) blockers.Add("proxy_cjk_text");

        return new ScanSnapshot(
            database.OriginalFileVersion.ToString(),
            counts,
            fonts,
            proxies,
            layouts,
            xrefs,
            blockers.ToArray(),
            sourceHashBefore,
            sourceHashBefore);
    }

    private static IReadOnlyList<FontSnapshot> ScanFonts(Database database, Transaction transaction)
    {
        var fonts = new List<FontSnapshot>();
        TextStyleTable styles = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
        foreach (ObjectId styleId in styles)
        {
            var style = (TextStyleTableRecord)transaction.GetObject(styleId, OpenMode.ForRead);
            AddFont(fonts, style.Name, style.FileName, database);
            AddFont(fonts, style.Name, style.BigFontFileName, database);
        }
        return fonts.OrderBy(font => font.Style, StringComparer.Ordinal).ThenBy(font => font.File, StringComparer.Ordinal).ToArray();
    }

    private static void AddFont(ICollection<FontSnapshot> fonts, string style, string? file, Database database)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return;
        }
        string resolvedPath = ResolveFont(file, database);
        fonts.Add(new FontSnapshot(style, file, !string.IsNullOrEmpty(resolvedPath)));
    }

    private static string ResolveFont(string file, Database database)
    {
        try
        {
            return HostApplicationServices.Current.FindFile(file, database, FindFileHint.Default);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return string.Empty;
        }
    }

    private static IReadOnlyList<LayoutSnapshot> ScanLayouts(Database database, Transaction transaction)
    {
        var layouts = new List<LayoutSnapshot>();
        var dictionary = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry entry in dictionary)
        {
            var layout = (Layout)transaction.GetObject(entry.Value, OpenMode.ForRead);
            layouts.Add(new LayoutSnapshot(layout.LayoutName, layout.BlockTableRecordId.Handle.ToString()));
        }
        return layouts.OrderBy(layout => layout.Name, StringComparer.Ordinal).ToArray();
    }
}

internal sealed record ScanSnapshot(
    string DrawingVersion,
    IReadOnlyDictionary<string, int> Counts,
    IReadOnlyList<FontSnapshot> Fonts,
    IReadOnlyList<ProxySnapshot> Proxies,
    IReadOnlyList<LayoutSnapshot> Layouts,
    IReadOnlyList<XrefSnapshot> Xrefs,
    IReadOnlyList<string> Blockers,
    string SourceSha256Before,
    string SourceSha256After);

internal sealed record FontSnapshot(string Style, string File, bool Exists);
internal sealed record ProxySnapshot(string Handle, string ClassName, bool HasCjkText);
internal sealed record LayoutSnapshot(string Name, string BlockHandle);

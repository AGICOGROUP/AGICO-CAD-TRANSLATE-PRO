using System.Reflection;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using CadTranslation.AutoCAD2025;

public sealed class StructureTests
{
    [CommandMethod("CAD_STRUCTURE_PROBE")]
    public void Run()
    {
        string source = Environment.GetEnvironmentVariable("CAD_STRUCTURE_SOURCE")!;
        string output = Environment.GetEnvironmentVariable("CAD_STRUCTURE_OUTPUT")!;
        string report = Environment.GetEnvironmentVariable("CAD_STRUCTURE_REPORT")!;
        string mode = Environment.GetEnvironmentVariable("CAD_STRUCTURE_MODE") ?? "save";
        try
        {
            if (mode != "compare")
            {
                using var db = NativeDrawing.Open(source);
                var previous = HostApplicationServices.WorkingDatabase;
                try
                {
                    HostApplicationServices.WorkingDatabase = db;
                    if (mode == "anonymous-swap")
                    {
                        using var tx = db.TransactionManager.StartTransaction();
                        var table = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
                        var lines = table.Cast<ObjectId>().Select(id => (BlockTableRecord)tx.GetObject(id, OpenMode.ForRead))
                            .Where(b => b.IsAnonymous && b.GetBlockReferenceIds(true, false).Count > 0)
                            .Select(b => b.Cast<ObjectId>().Select(id => tx.GetObject(id, OpenMode.ForRead)).OfType<Line>().FirstOrDefault())
                            .Where(l => l is not null).Cast<Line>().DistinctBy(l => l.GeometricExtents.ToString()).Take(2).ToArray();
                        if (lines.Length != 2) throw new InvalidOperationException("Fixture requires two referenced anonymous blocks with different lines.");
                        var a = lines[0]; var b = lines[1];
                        var start = a.StartPoint; var end = a.EndPoint;
                        a.UpgradeOpen(); b.UpgradeOpen();
                        a.StartPoint = b.StartPoint; a.EndPoint = b.EndPoint;
                        b.StartPoint = start; b.EndPoint = end;
                        tx.Commit();
                    }
                    if (mode is "move" or "delete")
                    {
                        using var tx = db.TransactionManager.StartTransaction();
                        var space = (BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                        var entity = space.Cast<ObjectId>().Select(id => tx.GetObject(id, OpenMode.ForRead)).OfType<Line>().First();
                        entity.UpgradeOpen();
                        if (mode == "delete") entity.Erase();
                        else entity.TransformBy(Autodesk.AutoCAD.Geometry.Matrix3d.Displacement(new(10, 20, 0)));
                        tx.Commit();
                    }
                    if (mode == "save-false") db.SaveAs(output, false, db.OriginalFileVersion, db.SecurityParameters);
                    else NativeDrawing.Save(db, output);
                }
                finally { HostApplicationServices.WorkingDatabase = previous; }
            }
            var capture = typeof(DrawingVerifier).GetMethod("CaptureSnapshot", BindingFlags.Static | BindingFlags.NonPublic)!;
            object Snapshot(string path, string label) => capture.Invoke(null, new object?[] {path,label,Path.GetDirectoryName(report)!,null})!;
            var before = Snapshot(source,"source");
            var after = Snapshot(output,"candidate");
            var rowsProperty = before.GetType().GetProperty("Rows")!;
            var first = (IReadOnlyDictionary<string,IReadOnlyList<string>>)rowsProperty.GetValue(before)!;
            var second = (IReadOnlyDictionary<string,IReadOnlyList<string>>)rowsProperty.GetValue(after)!;
            var differences = new Dictionary<string,object>();
            foreach (string key in new[]{"tables","nonText"})
            {
                var a = first[key]; var b = second[key];
                differences[key] = new { sourceCount=a.Count,candidateCount=b.Count,
                    removed=a.Except(b).Take(30).ToArray(), added=b.Except(a).Take(30).ToArray(),
                    removedCount=a.Except(b).Count(),addedCount=b.Except(a).Count(), equal=a.SequenceEqual(b) };
            }
            File.WriteAllText(report,JsonSerializer.Serialize(new {status=first["tables"].SequenceEqual(second["tables"]) && first["nonText"].SequenceEqual(second["nonText"])?"passed":"failed",mode,differences}));
            File.WriteAllText(report+".source.json",JsonSerializer.Serialize(first));
            File.WriteAllText(report+".candidate.json",JsonSerializer.Serialize(second));
        }
        catch (System.Exception ex) { File.WriteAllText(report,JsonSerializer.Serialize(new{status="error",error=ex.ToString()})); }
    }
}

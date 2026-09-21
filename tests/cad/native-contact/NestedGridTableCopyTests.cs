using System.Reflection;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CadTranslation.AutoCAD2025;
using CadTranslation.Contracts;
using CadTranslation.Core;

public class NestedGridTableCopyTests
{
    [CommandMethod("CAD_NESTED_GRID_COPY_TEST")]
    public void Run()
    {
        string report = Environment.GetEnvironmentVariable("CAD_CONTACT_TEST_REPORT")!;
        try
        {
            var pure = Check(report, false);
            var crossingEquipment = Check(report, false, crossingEquipment: true);
            var mixed = Check(report, true);
            var whollyNested = Check(report, false, true);
            var onePending = Check(report, false, false, true);
            var framed = Check(report, false, false, false, true);
            var twoRows = Check(report, true, twoRows: true);
            var longPanel = Check(report, true, longPanel: true);
            var unrelatedNestedText = Check(report, false, unrelatedNestedText: true);
            CheckReadableCellFit();
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", pure, mixed, whollyNested, onePending, framed, twoRows, longPanel, unrelatedNestedText, crossingEquipment }, JsonDefaults.Options));
        }
        catch (System.Exception error)
        { File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", assembly = typeof(BilingualTableCopy).Assembly.Location, error = error.ToString() })); }
    }

    private static void CheckReadableCellFit()
    {
        using var db=new Database(true,true);
        var previous=HostApplicationServices.WorkingDatabase;HostApplicationServices.WorkingDatabase=db;
        try
        {
            using var text=new MText();text.SetDatabaseDefaults(db);text.Attachment=AttachmentPoint.TopLeft;
            text.TextHeight=500;text.Width=4077;text.Location=new Point3d(800,780,0);text.Contents=@"\Ftxt.shx;连接点测试点";
            var fit=typeof(BilingualTableCopy).GetMethod("Fit",BindingFlags.NonPublic|BindingFlags.Static)!;
            Require((bool)fit.Invoke(null,[text,"Connection Point, Test Point",new Rect2(0,0,3400,1000),"en"])!,
                $"A readable complete two-line target must fit this 3400 x 1000 cell without changing the grid; height={text.TextHeight}, actual={text.ActualWidth}x{text.ActualHeight}, bounds={text.GeometricExtents}.");
            // MText GeometricExtents also includes the unused configured column;
            // this fixture is top-left attached, so native glyph size is direct.
            Require(text.Location.X>=0 && text.Location.Y-text.ActualHeight>=0 &&
                text.Location.X+text.ActualWidth<=3400 && text.Location.Y<=1000 && text.TextHeight>=300,
                "Native visible glyph bounds and readable height must stay inside their cell.");
        }
        finally{HostApplicationServices.WorkingDatabase=previous;}
    }

    // Real failure structure: table text and verticals belong to model space;
    // repeated body row lines belong to grandchildren of one anonymous-like block.
    private static object Check(string report, bool mixed, bool whollyNested = false, bool onePending = false, bool framed = false, bool twoRows = false, bool longPanel = false, bool unrelatedNestedText = false, bool crossingEquipment = false)
    {
        using var db = new Database(true, true);
        var previous = HostApplicationServices.WorkingDatabase;
        HostApplicationServices.WorkingDatabase = db;
        var receipts = new List<BilingualTableCopy.CopyReceipt>();
        string output = Path.Combine(Path.GetDirectoryName(report)!, framed ? "framed.dwg" : onePending ? "one-pending.dwg" : mixed ? "mixed.dwg" : whollyNested ? "wholly-nested-grid.dwg" : crossingEquipment ? "crossing-equipment.dwg" : unrelatedNestedText ? "unrelated-nested-text.dwg" : "nested-grid.dwg");
        string? targetBlockHandle = null, targetLineHandle = null;
        int rowCount = longPanel ? 14 : twoRows ? 2 : 3;
        double tableHeight = rowCount * 10;
        try
        {
            using (var tx = db.TransactionManager.StartTransaction())
            {
                var owner = (BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                var bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForWrite);
                var leaf = new BlockTableRecord { Name = "FixtureRowLeaf" };
                bt.Add(leaf); tx.AddNewlyCreatedDBObject(leaf, true);
                Add(leaf, new Line(new(0, 0, 0), new(40, 0, 0)));
                if (mixed) Add(leaf, new Circle(new(20, 0, 0), Vector3d.ZAxis, .25));
                var rows = new BlockTableRecord { Name = "FixtureNestedRows" };
                bt.Add(rows); tx.AddNewlyCreatedDBObject(rows, true);
                for (int i = 0; i < rowCount; i++) Add(rows, new BlockReference(new(0, i * 10, 0), leaf.ObjectId));
                var originalGrid=new BlockReference(Point3d.Origin, rows.ObjectId);
                Add(owner,originalGrid);
                if(unrelatedNestedText || crossingEquipment)
                {
                    var equipment=new BlockTableRecord { Name="FixtureEquipment" };
                    bt.Add(equipment);tx.AddNewlyCreatedDBObject(equipment,true);
                    Add(equipment,new MText { TextHeight=2, Contents="Silo", Location=new(2,4,0) });
                    Add(owner,new BlockReference(crossingEquipment ? new Point3d(-3,0,0) : Point3d.Origin,equipment.ObjectId));
                }
                var segments = new List<Segment2>();
                foreach (double x in new[] { 0d, 20d, 40d })
                { Add(whollyNested ? rows : owner, new Line(new(x, 0, 0), new(x, tableHeight, 0))); segments.Add(new(new(x, 0), new(x, tableHeight))); }
                Add(whollyNested ? rows : owner, new Line(new(0, tableHeight, 0), new(40, tableHeight, 0)));
                for (int i = 0; i <= rowCount; i++) segments.Add(new(new(0, i * 10), new(40, i * 10)));
                if(framed)
                    foreach(var line in new Segment2[]{new(new(-5,-5),new(45,-5)),new(new(45,-5),new(45,35)),new(new(45,35),new(-5,35)),new(new(-5,35),new(-5,-5))})
                    { Add(owner,new Line(new(line.Start.X,line.Start.Y,0),new(line.End.X,line.End.Y,0)));segments.Add(line); }
                var cells = Enumerable.Range(0, rowCount).SelectMany(i => new[] { new Rect2(0, i * 10, 20, i * 10 + 10), new Rect2(20, i * 10, 40, i * 10 + 10) }).ToArray();
                var texts = new List<CadLayoutText>(); var inputs = new List<LayoutWriteInput>();
                foreach (var cell in cells)
                {
                    var mt = new MText { TextHeight = 2.5, Width = 0, Attachment = AttachmentPoint.TopLeft,
                        Contents = @"\FArial;ABC", Location = new(cell.Right - 8, cell.Top - 2, 0) };
                    Add(owner, mt);
                    var ex = mt.GeometricExtents;
                    var box = new Rect2(ex.MinPoint.X, ex.MinPoint.Y, ex.MaxPoint.X, ex.MaxPoint.Y);
                    string id = mt.Handle.ToString();
                    var row = new ManifestRecord("1.0", id, "fixture", "model", id, "AcDbMText", "Contents", "text", mt.Contents, "ABC", mt.Contents, [],
                        new(new(mt.Location.X, mt.Location.Y, 0), null, 0, null),
                        new("0", "Standard", 2.5, 1, "", "", new Dictionary<string, string>()), id);
                    inputs.Add(new(mt.ObjectId, row, onePending && inputs.Count>0 ? "ABC" : "Flow Rate", true));
                    texts.Add(new(mt.ObjectId, id, owner.Name, id, "AcDbMText", "Flow Rate", true,
                        new(id, box, box.Center, 2.5), new("cell", LayoutRegionKind.TableCell, cell)));
                }
                // Deliberately supply projected segments: this test isolates copying
                // from the independent parent/child topology projection contract.
                var baseline = new CadLayoutBaseline([new(owner.Name, owner.Handle.ToString(), segments,
                    cells.Select(c => new LayoutRegion("cell", LayoutRegionKind.TableCell, c)).ToArray(), texts, [])], []);
                var decisions = new List<object>(); var blocked = new HashSet<string>();
                var handled = BilingualTableCopy.Apply(db, tx, baseline, inputs.ToArray(),
                    new() { [owner.Name] = texts.Select(t => t.Source.Bounds).ToList() }, "en", new(), receipts, decisions, new(), new(), blocked);
                if (mixed)
                {
                    Require(handled.Count == 0 && receipts.Count == 0 && blocked.Count == rowCount*2,
                        "A block containing non-grid graphics must not be cloned as a table grid.");
                    var obstacles = new Dictionary<string,List<Rect2>> { [owner.Name] = texts.Select(t=>t.Source.Bounds).ToList() };
                    var fallback = BilingualGroupLayout.Plan(db, baseline, inputs.ToArray(), obstacles, "en",
                        new CadObjectAccess(db,tx), decisions, blocked:blocked);
                    Require(fallback.Count==0 && blocked.Count==rowCount*2,
                        "A real table with unsupported grid members must remain an explicit complete-table repair, never an unframed text panel.");
                }
                else
                {
                    Require(handled.Count == (onePending ? 1 : 6) && blocked.Count == 0,
                        "All pending cells must be copied together with the complete nested row grid; " + JsonSerializer.Serialize(decisions, JsonDefaults.Options));
                    var blocks = receipts.Where(r => tx.GetObject(db.GetObjectId(false, new Handle(Convert.ToInt64(r.TargetHandle, 16)), 0), OpenMode.ForRead) is BlockReference).ToArray();
                    Require(blocks.Length == 1, "Keep the complete nested grid as one shared-definition block clone.");
                    targetBlockHandle = blocks[0].TargetHandle;
                    if(framed)
                    {
                        var copiedGrid=(BlockReference)tx.GetObject(db.GetObjectId(false,new Handle(Convert.ToInt64(targetBlockHandle,16)),0),OpenMode.ForRead);
                        Require(copiedGrid.GetPersistentReactorIds().Count==0,
                            "A grid supplement must not inherit the source's associative dependency/reactors.");
                    }
                    targetLineHandle = receipts.FirstOrDefault(r => tx.GetObject(db.GetObjectId(false, new Handle(Convert.ToInt64(r.TargetHandle, 16)), 0), OpenMode.ForRead) is Line)?.TargetHandle;
                    Require(receipts.Count == (whollyNested ? 7 : 11), "Expected complete nested grid plus all six texts.");
                    if(unrelatedNestedText || crossingEquipment)
                        Require(receipts.All(r=>r.Dx!=0 && Math.Abs(r.Dy)<1e-6),
                            "A complex equipment schedule needs one complete translated grid directly left or right of its source, not an above/below text panel.");
                    if(framed) Require(receipts.All(r=>r.Dx!=0 || r.Dy!=0),
                        "A supplement must remain separate from its source; crossing the frame is permitted.");
                    foreach (var input in inputs) Require(((MText)tx.GetObject(input.ObjectId, OpenMode.ForRead)).Text == "ABC", "Original text must remain unchanged.");
                }
                tx.Commit();
                void Add(BlockTableRecord ownerBlock, Entity entity)
                { ownerBlock.AppendEntity(entity); tx.AddNewlyCreatedDBObject(entity, true); }
            }
            NativeDrawing.Save(db, output);
        }
        finally { HostApplicationServices.WorkingDatabase = previous; }
        if (!mixed)
        {
            // JSON round-trip exercises the durable receipt path, not just live objects.
            var restored = JsonSerializer.Deserialize<List<BilingualTableCopy.CopyReceipt>>(JsonSerializer.Serialize(receipts, JsonDefaults.Options), JsonDefaults.Options)!;
            Verify(output, restored);
            RequireRejected(() => Verify(output, restored.Select(r => r with { Table = new(0, 0, 0, 0) }).ToArray()), "Empty table bounds must not activate permissive verification.");
            if (targetLineHandle is not null)
            {
                string shortened = Alter(output, "shortened.dwg", targetLineHandle, e => { var line = (Line)e; line.EndPoint += (line.StartPoint - line.EndPoint).GetNormal() * .5; });
                RequireRejected(() => Verify(shortened, restored), "Shortened copied line must fail exact clipped geometry verification.");
            }
            string movedBlock = Alter(output, "moved-block.dwg", targetBlockHandle!, e => ((BlockReference)e).Rotation += .1);
            RequireRejected(() => Verify(movedBlock, restored), "Rotated copied grid must fail transform verification.");
        }
        return new { mixed, whollyNested, unrelatedNestedText, crossingEquipment, copies = receipts.Count, output };
    }

    private static string Alter(string source, string name, string handle, Action<Entity> change)
    {
        string output = Path.Combine(Path.GetDirectoryName(source)!, name);
        using var db = NativeDrawing.Open(source);
        using (var tx = db.TransactionManager.StartTransaction())
        { change((Entity)tx.GetObject(db.GetObjectId(false, new Handle(Convert.ToInt64(handle, 16)), 0), OpenMode.ForWrite)); tx.Commit(); }
        NativeDrawing.Save(db, output); return output;
    }
    private static void Verify(string output, IReadOnlyList<BilingualTableCopy.CopyReceipt> receipts)
    {
        var config = JsonSerializer.Deserialize<JobConfig>(JsonSerializer.Serialize(new { outputPath = output }), JsonDefaults.Options)!;
        var context = (JobContext)Activator.CreateInstance(typeof(JobContext), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [config, Path.GetDirectoryName(output)!], null)!;
        BilingualTableCopy.Verify(context, receipts);
    }
    private static void RequireRejected(Action action, string message)
    { try { action(); } catch (CommandProtocolException) { return; } throw new InvalidOperationException(message); }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}

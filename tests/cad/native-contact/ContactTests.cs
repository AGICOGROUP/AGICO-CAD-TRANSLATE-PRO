using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CadTranslation.Core;
using CadTranslation.AutoCAD2025;

public class ContactTests
{
    [CommandMethod("CAD_CONTACT_TEST")]
    public void Run()
    {
        var report = Environment.GetEnvironmentVariable("CAD_CONTACT_TEST_REPORT")!;
        try
        {
            using var circle = new Circle(Point3d.Origin, Vector3d.ZAxis, 10);
            Assert(NativeGeometryContact.Intersects(circle, new Rect2(-1,-1,1,1)) == false, "circle empty center is not a stroke");
            Assert(NativeGeometryContact.Intersects(circle, new Rect2(9,-1,11,1)) == true, "circle crossing must be detected");
            Assert(NativeGeometryContact.Intersects(circle, new Rect2(-11,-11,11,11)) == true, "enclosed curve must be detected");
            using var arc = new Arc(Point3d.Origin, 10, 0, Math.PI / 2);
            Assert(NativeGeometryContact.Intersects(arc, new Rect2(1,1,2,2)) == false, "arc bbox interior is not a stroke");
            using var line = new Line(new Point3d(0,0,0), new Point3d(10,10,0));
            Assert(NativeGeometryContact.Intersects(line, new Rect2(0,8,2,10)) == false, "diagonal bbox false alarm");
            Assert(NativeGeometryContact.Intersects(line, new Rect2(4,4,6,6)) == true, "diagonal true crossing");
            using var dimension = new RotatedDimension(0, new Point3d(0,0,0), new Point3d(20,0,0), new Point3d(10,10,0), "", ObjectId.Null);
            Assert(NativeGeometryContact.Intersects(dimension, new Rect2(1,3,2,4)) != true, "dimension interior cannot be confirmed from its box");
            LocalCorrection(false);
            LocalCorrection(true);
            using var wide = new Polyline();
            wide.AddVertexAt(0,new Point2d(0,0),0,4,4);
            wide.AddVertexAt(1,new Point2d(10,10),0,4,4);
            Assert(NativeGeometryContact.Intersects(wide,new Rect2(0,1,1,2)) is null,"wide polyline cannot be cleared by center-line testing");
            File.WriteAllText(report, "{\"status\":\"passed\",\"cases\":10}");
        }
        catch (System.Exception ex) { File.WriteAllText(report, System.Text.Json.JsonSerializer.Serialize(new {status="failed", error=ex.ToString()})); }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static void LocalCorrection(bool tightCell)
    {
        using var db = new Database(true, true);
        ObjectId first, second;
        Rect2 candidate, neighbor;
        using (var tx = db.TransactionManager.StartTransaction())
        {
            var table = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
            var model = (BlockTableRecord)tx.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            var a = new MText { Contents="Hydraulic Pump", TextHeight=2, Width=0, Location=Point3d.Origin };
            var b = new MText { Contents="CLOSED", TextHeight=2, Width=0, Location=new Point3d(8,0,0) };
            first=model.AppendEntity(a); tx.AddNewlyCreatedDBObject(a,true);
            second=model.AppendEntity(b); tx.AddNewlyCreatedDBObject(b,true);
            candidate=Box(a); neighbor=Box(b);
            Assert(NativeGeometryContact.BoundsOverlap(candidate,neighbor), "fixture must overlap before correction");
            tx.Commit();
        }
        var sourceBox = new Rect2(candidate.Left, candidate.Bottom, candidate.Left+2, candidate.Top);
        LayoutRegion? region = tightCell ? new LayoutRegion("cell",LayoutRegionKind.TableCell,sourceBox) : null;
        var texts = new[] {
            new CadLayoutText(first,"a","*Model_Space",first.Handle.ToString(),"AcDbMText","Hydraulic Pump",true,
                new TextLayoutSnapshot("a",sourceBox,sourceBox.Center,2),region),
            new CadLayoutText(second,"b","*Model_Space",second.Handle.ToString(),"AcDbMText","CLOSED",false,
                new TextLayoutSnapshot("b",neighbor,neighbor.Center,2),null)
        };
        var baseline = new CadLayoutBaseline(new[] {new CadDefinitionTopology("*Model_Space","0",Array.Empty<Segment2>(),
            region == null ? Array.Empty<LayoutRegion>() : new[]{region},texts,Array.Empty<CadProtectedGeometry>())},Array.Empty<BlockInstancePath>());
        var rows=texts.Select(t=>new LayoutAuditTextRow(t.RecordId,t.DefinitionName,t.Region?.Id??"",t.EntityHandle,t.EntityHandle,
            t.Source.Anchor,t.Source.Anchor,t.Source.Bounds,t.RecordId=="a"?candidate:neighbor,Array.Empty<string>())).ToArray();
        var risk=new LayoutAuditRiskRow("text-overlap","high","a","b","*Model_Space","","",1,candidate,"test collision");
        var audit=new LayoutAuditReport(1,true,0,0,0,0,0,Array.Empty<string>(),new Dictionary<string,int>(),new Dictionary<string,string>(),rows,new[]{risk},new[]{risk});
        var layout=new LayoutOptimizationResult(1,0,0,0,0,0,0,0,Array.Empty<string>(),Array.Empty<LayoutAdjustment>());
        ReplaceImportGate.EnsurePassed(layout,audit); // A layout suspicion must reach final visual assessment.
        bool rejected=false;
        try { ReplaceImportGate.EnsurePassed(layout with {UncoveredRecordIds=new[]{"a"}},audit); }
        catch (CadTranslation.AutoCAD2025.CommandProtocolException) { rejected=true; }
        Assert(rejected,"missing coverage is still a hard failure");
        string original=Path.ChangeExtension(Environment.GetEnvironmentVariable("CAD_CONTACT_TEST_REPORT")!,tightCell?"tight.dwg":"local.dwg");
        db.SaveAs(original,true,DwgVersion.AC1032,db.SecurityParameters);
        var results=ReplaceLocalCorrection.Apply(db,baseline,audit);
        using (var verify=db.TransactionManager.StartTransaction())
        {
        var corrected=(MText)verify.GetObject(first,OpenMode.ForRead);
        var untouched=(MText)verify.GetObject(second,OpenMode.ForRead);
        Assert(corrected.Contents=="Hydraulic Pump" && untouched.Contents=="CLOSED" && Box(untouched)==neighbor,"content and unrelated text must be unchanged");
        if (tightCell)
            Assert(results.Length==0 && Box(corrected)==candidate && corrected.TextHeight==2 && corrected.Width==0,
                $"unplaceable correction must roll back: count={results.Length}, before={candidate}, after={Box(corrected)}, height={corrected.TextHeight}, width={corrected.Width}");
        else
            Assert(results.Length==1 && !NativeGeometryContact.BoundsOverlap(Box(corrected),neighbor),"native local correction must eliminate the overlap");
        }
        string correctedPath=Path.ChangeExtension(original,"corrected.dwg");
        db.SaveAs(correctedPath,true,DwgVersion.AC1032,db.SecurityParameters);
        File.Copy(correctedPath,original,true);
        using var reopened=new Database(false,true);
        reopened.ReadDwgFile(original,FileOpenMode.OpenForReadAndAllShare,true,"");
        reopened.CloseInput(true);
        using var check=reopened.TransactionManager.StartTransaction();
        var final=(MText)check.GetObject(reopened.GetObjectId(false,first.Handle,0),OpenMode.ForRead);
        Assert(final.Contents=="Hydraulic Pump" && (tightCell ? Box(final)==candidate : !NativeGeometryContact.BoundsOverlap(Box(final),neighbor)),
            "saved and reopened candidate must preserve the accepted decision");
    }
    private static Rect2 Box(Entity entity)
    {
        var b=CadLayoutGeometry.TryBounds(entity)!;
        return new Rect2(b.MinX,b.MinY,b.MaxX,b.MaxY);
    }
}

using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CadTranslation.AutoCAD2025;
using CadTranslation.Contracts;
using CadTranslation.Core;

public class TablePlacementTests
{
    [CommandMethod("CAD_TABLE_STRATEGY_TEST")]
    public void Run()
    {
        string report=Environment.GetEnvironmentVariable("CAD_CONTACT_TEST_REPORT")!;
        try
        {
            var results=new List<object>();
            foreach(bool blockAll in new[]{false,true}) results.Add(Check(blockAll,report));
            File.WriteAllText(report,JsonSerializer.Serialize(new{status="passed",results},JsonDefaults.Options));
        }
        catch(System.Exception e){File.WriteAllText(report,JsonSerializer.Serialize(new{status="failed",error=e.ToString()}));}
    }

    private static object Check(bool blockAll,string report)
    {
        using var db=new Database(true,true);
        var previous=HostApplicationServices.WorkingDatabase;
        try
        {
            HostApplicationServices.WorkingDatabase=db;
            using var tx=db.TransactionManager.StartTransaction();
            var owner=(BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db),OpenMode.ForWrite);
            Rect2[] title=[new(0,0,30,10),new(30,0,100,10),new(0,10,20,25),new(20,10,100,25)];
            var regular=Enumerable.Range(0,4).SelectMany(i=>new[]{new Rect2(0,25+i*10,10,35+i*10),new Rect2(10,25+i*10,100,35+i*10)}).ToArray();
            var cells=title.Concat(regular).ToArray();
            var segments=cells.SelectMany(c=>new[]{new Segment2(new(c.Left,c.Bottom),new(c.Right,c.Bottom)),
                new Segment2(new(c.Left,c.Top),new(c.Right,c.Top)),new Segment2(new(c.Left,c.Bottom),new(c.Left,c.Top)),
                new Segment2(new(c.Right,c.Bottom),new(c.Right,c.Top))}).Distinct().ToArray();
            foreach(var s in segments)
            {
                var line=new Line(new(s.Start.X,s.Start.Y,0),new(s.End.X,s.End.Y,0));
                owner.AppendEntity(line);tx.AddNewlyCreatedDBObject(line,true);
            }
            var texts=new List<CadLayoutText>();var inputs=new List<LayoutWriteInput>();
            foreach(var c in cells)
            {
                bool isTitle=c.Top<=25, index=!isTitle && c.Left==0;
                string raw=index?"1":isTitle?"图名":"水泵",target=index?"1":isTitle?"Drawing Title":"Water Pump";
                var text=new MText();text.SetDatabaseDefaults(db);text.TextHeight=2;text.Width=0;
                text.Attachment=AttachmentPoint.TopLeft;text.Contents=@"\FSimSun;"+raw;
                text.Location=new Point3d(isTitle?c.Right-6:c.Left+1,c.Center.Y+1,0);
                owner.AppendEntity(text);tx.AddNewlyCreatedDBObject(text,true);
                var b=CadLayoutGeometry.TryFreshBounds(text)!;var box=new Rect2(b.MinX,b.MinY,b.MaxX,b.MaxY);
                string id=text.Handle.ToString();
                var row=new ManifestRecord("1.0",id,"fixture","model",id,"AcDbMText","Contents","text",text.Contents,raw,raw,[],
                    new(new(text.Location.X,text.Location.Y,0),null,0,null),new("0","Standard",2,1,"","",new Dictionary<string,string>()),id);
                inputs.Add(new(text.ObjectId,row,target,raw!=target));
                texts.Add(new(text.ObjectId,id,owner.Name,id,"AcDbMText",target,raw!=target,new(id,box,box.Center,2),
                    new("cell",LayoutRegionKind.TableCell,c)));
            }
            var baseline=DrawingTopologyCapture.Capture(db,tx,inputs.ToArray(),new CadObjectAccess(db,tx));
            var occupied=new Dictionary<string,List<Rect2>>{{owner.Name,texts.Select(t=>t.Source.Bounds).ToList()}};
            // The source schedule already blocks the top; do not invent a blocker over its cells.
            if(blockAll) occupied[owner.Name].AddRange(BilingualTablePlacement.AdjacentCopies(new(0,0,100,25),3.75).Where(c=>c.Bottom<25));
            var pairs=new List<BilingualDrawingImporter.Pair>();var receipts=new List<BilingualTableCopy.CopyReceipt>();
            var decisions=new List<object>();var slots=new Dictionary<string,BilingualGroupLayout.Slot>();
            var ids=new HashSet<string>();var blocked=new HashSet<string>();
            var handled=BilingualTableCopy.Apply(db,tx,baseline,inputs.ToArray(),occupied,"en",pairs,receipts,decisions,slots,ids,blocked);
            Require(slots.Count==4,"All four roomy description cells must keep a right-hand inline translation: "+JsonSerializer.Serialize(new{slots,decisions,blocked},JsonDefaults.Options));
            Require(slots.All(p=>p.Value.Strategy=="table-inline-right" && p.Value.Bounds.Left>texts.Single(t=>t.RecordId==p.Key).Source.Bounds.Right),"Inline targets must follow their source.");
            Require(blockAll?blocked.Count==4 && receipts.Count==0:handled.Count==4 && blocked.Count==0 && receipts.Count>4,
                "The entire irregular title must be copied adjacent or reported blocked, without scattering.");
            foreach(var copy in receipts)
            {
                Require(copy.Table==new Rect2(0,0,100,25),"The full merged title panel must be copied, not just a repeated row-height subset.");
                Require((copy.Dx==0) != (copy.Dy==0),"The copy must use one axis, never a diagonal.");
            }
            foreach(var source in texts)
                Require(((MText)tx.GetObject(source.ObjectId,OpenMode.ForRead)).Contents==inputs.Single(i=>i.ObjectId==source.ObjectId).Manifest.RawText,"Source text changed.");
            foreach(var slot in slots.Values)
            {
                var text=new MText();text.SetDatabaseDefaults(db);text.Attachment=AttachmentPoint.TopLeft;
                text.TextHeight=slot.Height;text.Width=slot.WrapWidth;text.Contents=BilingualGroupLayout.Contents(slot.DisplayText,"en");
                var fp=BilingualPlacementChecks.Footprint(text);BilingualPlacementChecks.Move(text,fp,slot.Bounds.Left,slot.Bounds.Top,0);
                owner.AppendEntity(text);tx.AddNewlyCreatedDBObject(text,true);
            }
            if(!blockAll)
            {
                foreach(ObjectId id in owner)
                {
                    if(inputs.Any(i=>i.ObjectId==id) || tx.GetObject(id,OpenMode.ForRead) is not MText mt) continue;
                    string handle=mt.Handle.ToString();
                    var row=new ManifestRecord("1.0",handle,"fixture","model",handle,"AcDbMText","Contents","text",mt.Contents,mt.Text,mt.Contents,[],
                        new(new(mt.Location.X,mt.Location.Y,0),null,0,null),new("0","Standard",mt.TextHeight,1,"","",new Dictionary<string,string>()),handle);
                    inputs.Add(new(id,row,mt.Contents,false));
                }
                var nextBaseline=DrawingTopologyCapture.Capture(db,tx,inputs.ToArray(),new CadObjectAccess(db,tx));
                var nextOccupied=nextBaseline.Definitions.ToDictionary(d=>d.Name,d=>d.Texts.Select(t=>t.Source.Bounds).ToList());
                var nextCopies=new List<BilingualTableCopy.CopyReceipt>();var nextSlots=new Dictionary<string,BilingualGroupLayout.Slot>();
                BilingualTableCopy.Apply(db,tx,nextBaseline,inputs.ToArray(),nextOccupied,"en",new(),nextCopies,new(),nextSlots,new(),new());
                Require(nextCopies.Count==0 && nextSlots.Count==0,"A second pass must reuse both the inline text and the translated table without duplication.");
            }
            tx.Commit();
            string output=Path.Combine(Path.GetDirectoryName(report)!,blockAll?"blocked.dwg":"table-strategies.dwg");
            NativeDrawing.Save(db,output);
            return new{blockAll,inline=slots.Count,copied=handled.Count,entities=receipts.Count,blocked=blocked.Count,output,decisions};
        }
        finally{HostApplicationServices.WorkingDatabase=previous;}
    }
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}

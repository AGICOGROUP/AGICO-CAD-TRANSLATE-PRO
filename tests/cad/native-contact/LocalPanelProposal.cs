using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CadTranslation.AutoCAD2025;
using CadTranslation.Contracts;
using CadTranslation.Core;
using System.Reflection;
using System.Text.Json;

// Geometry-only regression aid: emit edits for the existing correct command.
// Never saves a DWG, rewrites translations, or creates an acceptance receipt.
public class LocalPanelProposal
{
    [CommandMethod("CAD_LOCAL_PANEL_PROPOSAL")]
    public void Run()
    {
        string report=Environment.GetEnvironmentVariable("CAD_CONTACT_TEST_REPORT")!;
        try
        {
            string job=Environment.GetEnvironmentVariable("CAD_LAYOUT_REVIEW_JOB")!;
            using var final=JsonDocument.Parse(File.ReadAllText(Path.Combine(job,"artifacts/bilingual-final.json")));
            using var pairDoc=JsonDocument.Parse(File.ReadAllText(Path.Combine(job,"artifacts/bilingual-pairs.json")));
            using var groupDoc=JsonDocument.Parse(File.ReadAllText(Path.Combine(job,"artifacts/bilingual-table-layout.json")));
            var pairs=JsonSerializer.Deserialize<BilingualDrawingImporter.Pair[]>(pairDoc.RootElement.GetProperty("pairs").GetRawText(),JsonDefaults.Options)!.ToDictionary(p=>p.RecordId);
            var original=NativeDrawing.ReadRows<ManifestRecord>(Path.Combine(job,"exchange/manifest.input.jsonl")).ToDictionary(r=>r.RecordId);
            var candidate=NativeDrawing.ReadRows<ManifestRecord>(Path.Combine(job,"artifacts/bilingual-candidate.jsonl"));
            var selectedHandles=(Environment.GetEnvironmentVariable("CAD_LAYOUT_TARGET_HANDLES")??"").Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var groups=groupDoc.RootElement.GetProperty("tables").EnumerateArray()
                .Where(g=>g.TryGetProperty("destination",out _) && g.GetProperty("strategy").GetString() is "table-aligned-block" or "note-block")
                .Select(g=>g.GetProperty("recordIds").EnumerateArray().Select(v=>v.GetString()!).Where(id=>pairs[id].Decision=="added" &&
                    (selectedHandles.Count==0 || selectedHandles.Contains(pairs[id].TargetHandle))).ToArray()).Where(g=>g.Length>0).ToArray();
            var moving=groups.SelectMany(g=>g).Select(id=>pairs[id].TargetHandle).ToHashSet();
            using var db=NativeDrawing.Open(final.RootElement.GetProperty("candidatePath").GetString()!);
            var previous=HostApplicationServices.WorkingDatabase;
            try
            {
                HostApplicationServices.WorkingDatabase=db;
                using var tx=db.TransactionManager.StartTransaction();
                var access=new CadObjectAccess(db,tx);
                var instances=BlockInstanceWalker.Capture(db,tx,access);
                Entity Entity(string h)=>(Entity)tx.GetObject(db.GetObjectId(false,new Handle(Convert.ToInt64(h,16)),0),OpenMode.ForRead);
                string Owner(Entity e)=>((BlockTableRecord)tx.GetObject(e.OwnerId,OpenMode.ForRead)).Name;
                Rect2 Box(Entity e,ManifestRecord row)
                {
                    if(e is Dimension d)
                    {
                        using var style=d.GetDimstyleData(); double h=Math.Max(style.Dimtxt*Math.Max(d.Dimscale,1),1e-6);
                        return TextBoundsEstimator.Estimate(new Point2(d.TextPosition.X,d.TextPosition.Y),h,1,row.PlainText,"TextCenter","TextVerticalMid",row.Geometry.RotationRadians);
                    }
                    var b=e is MText m?CadLayoutGeometry.TryFreshBounds(m):CadLayoutGeometry.TryBounds(e);
                    if(b is null && e is DBText dt)
                    {
                        var anchor=dt.IsDefaultAlignment?dt.Position:dt.AlignmentPoint;
                        return TextBoundsEstimator.Estimate(new Point2(anchor.X,anchor.Y),dt.Height,dt.WidthFactor,
                            row.RawText,dt.HorizontalMode.ToString(),dt.VerticalMode.ToString(),dt.Rotation);
                    }
                    if(b is null) throw new InvalidOperationException("Unmeasured text "+row.Handle);
                    return new Rect2(b.MinX,b.MinY,b.MaxX,b.MaxY);
                }
                var objects=candidate.Select(r=>(Row:r,Entity:Entity(r.Handle))).Where(x=>x.Entity is DBText or MText or Dimension).ToArray();
                var owners=objects.Select(x=>Owner(x.Entity)).Distinct().ToArray();
                var occupied=owners.ToDictionary(n=>n,n=>new List<Rect2>());
                void Reserve(string owner,Rect2 b)
                {
                    occupied[owner].Add(b);
                    foreach(string other in owners.Where(n=>n!=owner)) occupied[other].AddRange(InstanceOccupancyProjection.Project(b,owner,other,instances));
                }
                foreach(var x in objects.Where(x=>!moving.Contains(x.Row.Handle))) Reserve(Owner(x.Entity),Box(x.Entity,x.Row));
                Rect2 Source(string id)=>Box(Entity(pairs[id].SourceHandle),original[id]);
                Rect2 Union(IEnumerable<Rect2> boxes){var a=boxes.ToArray();return new(a.Min(b=>b.Left),a.Min(b=>b.Bottom),a.Max(b=>b.Right),a.Max(b=>b.Top));}
                var edits=new List<object>();
                var decisions=new List<object>();
                foreach(var ids in groups.OrderByDescending(g=>Union(g.Select(Source)).Area))
                {
                    string owner=Owner(Entity(pairs[ids[0]].SourceHandle));
                    var source=Union(ids.Select(Source));
                    bool local=ids.Length==1 || ids.Select(id=>BilingualDrawingImporter.Plain(original[id].RawText)).Any(s=>
                        BilingualTitlePanel.IsTitleField(s) || System.Text.RegularExpressions.Regex.IsMatch(s,@"^\s*(工程|阶段|名称|设计)\s*$"));
                    if(local)
                    {
                        foreach(string id in ids)
                        {
                            var row=original[id]; var b=Source(id); var target=(MText)Entity(pairs[id].TargetHandle);
                            using var probe=(MText)target.Clone();
                            var text=new CadLayoutText(Entity(row.Handle).ObjectId,id,owner,row.Handle,row.ObjectType,pairs[id].TargetText,true,new(id,b,b.Center,row.Properties.Height),null);
                            var definition=new CadDefinitionTopology(owner,"",[],[],[text],[]);
                            object?[] args={probe,target.Contents,text,definition,occupied[owner],target.Location.Z,b,0d,new BilingualPlacementTrace(),false};
                            if(!(bool)typeof(BilingualDrawingImporter).GetMethod("Place",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,args)!) throw new InvalidOperationException("No local placement "+row.Handle);
                            // Preserve the exact raw text: Place may add width formatting,
                            // so restore contents and remeasure before proposing geometry.
                            probe.Contents=target.Contents;
                            var fp=BilingualPlacementChecks.Footprint(probe);
                            var actual=new Rect2(probe.Location.X+fp.Left,probe.Location.Y+fp.Bottom,probe.Location.X+fp.Right,probe.Location.Y+fp.Top);
                            if(occupied[owner].Any(o=>BilingualDrawingImporter.Intersects(actual,o,probe.TextHeight*.12))) throw new InvalidOperationException("Local restored text collision "+row.Handle);
                            edits.Add(new{handle=target.Handle.ToString(),expectedText=target.Contents,width=Math.Max(probe.Width,fp.Width*1.05),height=probe.TextHeight,x=probe.Location.X,y=probe.Location.Y});
                            Reserve(owner,actual);
                        }
                        continue;
                    }
                    double sourceHeight=ids.Min(id=>original[id].Properties.Height);
                    var ordered=ids.OrderByDescending(id=>Math.Round(Source(id).Center.Y/(sourceHeight*.5))).ThenBy(id=>Source(id).Left).ToArray();
                    List<(string Id,Rect2 Box,double Width,double Height,Point3d Location)>? best=null;
                    double bestGap=double.PositiveInfinity;
                    int columns=ordered.Length>24?2:1, rowsPerColumn=(ordered.Length+columns-1)/columns;
                    var allowed=new Rect2(source.Left-source.Width*3-sourceHeight*60,source.Bottom-source.Height*3-sourceHeight*60,source.Right+source.Width*3+sourceHeight*60,source.Top+source.Height*3+sourceHeight*60);
                    foreach(double scale in new[]{.85,.7})
                    foreach(double width in new[]{Math.Max(source.Width/columns,sourceHeight*24),Math.Max(source.Width*.75/columns,sourceHeight*18),Math.Max(source.Width*1.4/columns,sourceHeight*30)})
                    {
                        var sizes=new List<Rect2>();
                        foreach(string id in ordered)
                        {
                            using var probe=(MText)Entity(pairs[id].TargetHandle).Clone(); probe.Width=width;probe.TextHeight=sourceHeight*scale;
                            sizes.Add(BilingualPlacementChecks.Footprint(probe));
                        }
                        double gap=sourceHeight*scale*.45;
                        var widths=Enumerable.Range(0,columns).Select(c=>sizes.Skip(c*rowsPerColumn).Take(rowsPerColumn).Max(b=>b.Width)).ToArray();
                        double w=widths.Sum()+gap*4*(columns-1),h=Enumerable.Range(0,columns).Max(c=>sizes.Skip(c*rowsPerColumn).Take(rowsPerColumn).Sum(b=>b.Height+gap)-gap);
                        foreach(var dst in BilingualPanelPlacement.Destinations(source,w,h,sourceHeight,allowed,null))
                        {
                            double distance=BilingualLocalPlacement.Gap(source,dst);
                            if(distance>=bestGap || occupied[owner].Any(o=>BilingualDrawingImporter.Intersects(dst,o,sourceHeight*.15))) continue;
                            double top=dst.Top;var proposed=new List<(string,Rect2,double,double,Point3d)>();
                            for(int i=0;i<ordered.Length;i++)
                            {
                                int column=i/rowsPerColumn; if(i%rowsPerColumn==0)top=dst.Top;
                                double left=dst.Left+widths.Take(column).Sum()+gap*4*column;
                                var fp=sizes[i]; var b=new Rect2(left,top-fp.Height,left+fp.Width,top);
                                proposed.Add((ordered[i],b,width,sourceHeight*scale,new Point3d(left-fp.Left,top-fp.Top,((MText)Entity(pairs[ordered[i]].TargetHandle)).Location.Z)));
                                top-=fp.Height+gap;
                            }
                            best=proposed;bestGap=distance;break;
                        }
                    }
                    if(best is null) throw new InvalidOperationException("No panel placement "+pairs[ids[0]].SourceHandle);
                    foreach(var p in best)
                    {
                        var target=(MText)Entity(pairs[p.Id].TargetHandle);
                        edits.Add(new{handle=target.Handle.ToString(),expectedText=target.Contents,width=p.Width,height=p.Height,x=p.Location.X,y=p.Location.Y});
                        Reserve(owner,p.Box);
                    }
                    decisions.Add(new{source,destination=Union(best.Select(p=>p.Box)),count=ids.Length,bestGap});
                }
                File.WriteAllText(report+".corrections.json",JsonSerializer.Serialize(new{candidateSha256=final.RootElement.GetProperty("candidateSha256").GetString(),edits},JsonDefaults.Options));
                File.WriteAllText(report,JsonSerializer.Serialize(new{status="passed",count=edits.Count,decisions},JsonDefaults.Options));
            }
            finally {HostApplicationServices.WorkingDatabase=previous;}
        }
        catch(System.Exception e){File.WriteAllText(report,JsonSerializer.Serialize(new{status="failed",error=e.ToString()}));}
    }
}

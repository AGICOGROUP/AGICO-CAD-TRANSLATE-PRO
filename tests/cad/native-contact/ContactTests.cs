using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CadTranslation.Core;
using CadTranslation.AutoCAD2025;

public class ContactTests
{
    [CommandMethod("CAD_NEAR_LABEL_PROBE")]
    public void ProbeNearby()
    {
        string job=Environment.GetEnvironmentVariable("CAD_LAYOUT_REVIEW_JOB")!;
        string report=Environment.GetEnvironmentVariable("CAD_CONTACT_TEST_REPORT")!;
        try
        {
            var rows=NativeDrawing.ReadRows<CadTranslation.Contracts.ManifestRecord>(Path.Combine(job,"exchange/manifest.input.jsonl"));
            using var pairsJson=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(job,"artifacts/bilingual-pairs.json")));
            var pairs=System.Text.Json.JsonSerializer.Deserialize<BilingualDrawingImporter.Pair[]>(pairsJson.RootElement.GetProperty("pairs").GetRawText(),CadTranslation.Contracts.JsonDefaults.Options)!;
            var selected=pairs.Where(p=>new[]{"9E87EF","9E8BAB","9E7674"}.Contains(p.SourceHandle)).ToArray();
            Assert(selected.Length==3,"the local probe requires all three labels from its fixed drawing fixture");
            var excluded=selected.Select(p=>p.TargetHandle).ToHashSet();
            using var db=NativeDrawing.Open(Path.Combine(job,"results/candidate.dwg"));
            var prior=HostApplicationServices.WorkingDatabase;
            try
            {
                HostApplicationServices.WorkingDatabase=db;
                using var tx=db.TransactionManager.StartTransaction();
                var access=new CadObjectAccess(db,tx,new List<CadObjectAccess.Issue>());
                var inputs=rows.Select(r=>new LayoutWriteInput(db.GetObjectId(false,new Handle(Convert.ToInt64(r.Handle,16)),0),r,
                    pairs.FirstOrDefault(p=>p.RecordId==r.RecordId)?.TargetText??r.RawText,false)).ToArray();
                var baseline=DrawingTopologyCapture.Capture(db,tx,inputs,access);
                baseline=baseline with {Definitions=baseline.Definitions.Select(d=>d with {Texts=d.Texts.Where(t=>!excluded.Contains(t.EntityHandle)).ToArray()}).ToArray()};
                var definitions=baseline.Definitions.ToDictionary(d=>d.Name);
                typeof(BilingualDrawingImporter).GetMethod("ProjectNearbyBoundaries",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!.Invoke(null,new object[]{definitions,baseline,inputs});
                var results=new List<object>();
                foreach(var pair in selected)
                {
                    var source=baseline.Definitions.SelectMany(d=>d.Texts).Single(t=>t.EntityHandle==pair.SourceHandle);
                    var row=rows.Single(r=>r.Handle==pair.SourceHandle);
                    var original=(DBText)tx.GetObject(source.ObjectId,OpenMode.ForRead);
                    using var text=new MText();text.SetDatabaseDefaults(db);text.Attachment=AttachmentPoint.TopLeft;
                    text.TextStyleId=original.TextStyleId;text.Normal=original.Normal;text.Rotation=original.Rotation;
                    var occupied=baseline.Definitions.SelectMany(d=>d.Texts.SelectMany(t=>d.Name==source.DefinitionName
                        ? new[]{t.Source.Bounds}:InstanceOccupancyProjection.Project(t.Source.Bounds,d.Name,source.DefinitionName,baseline.BlockInstances))).ToList();
                    var trace=new BilingualPlacementTrace();
                    object?[] args={text,pair.TargetText,source,definitions[source.DefinitionName],occupied,row.Geometry.InsertionPoint.Z,source.Source.Bounds,0d,trace,false};
                    bool placed=(bool)typeof(BilingualDrawingImporter).GetMethod("Place",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!.Invoke(null,args)!;
                    var trials=new List<object>();
                    var def=definitions[source.DefinitionName];
                    var hints=def.BoundarySegments.Select(s=>new Rect2(s.MinX,s.MinY,s.MaxX,s.MaxY))
                        .Where(b=>BilingualDrawingImporter.Intersects(trace.Allowed,b,.001)).Distinct()
                        .OrderBy(b=>BilingualLocalPlacement.Gap(source.Source.Bounds,b)).Take(24).ToArray();
                    foreach(double sc in new[]{.45,.35})
                    foreach(double factor in new[]{1.0,.8})
                    foreach(double widthFactor in new[]{.8,.5,.35})
                    {
                        text.TextHeight=source.Source.OriginalTextHeight*sc;
                        text.Contents="{\\W"+factor.ToString(System.Globalization.CultureInfo.InvariantCulture)+";"+pair.TargetText+"}";
                        text.Width=Math.Max(source.Source.OriginalTextHeight,BilingualPlacementChecks.AlongText(source.Source.Bounds,text.Rotation)*widthFactor-source.Source.OriginalTextHeight*.24);
                        var fp=BilingualPlacementChecks.Footprint(text);
                        var slots=BilingualLocalPlacement.Candidates(trace.Allowed,source.Source.Bounds,fp.Width,fp.Height,occupied,source.Source.OriginalTextHeight*.12,hints,
                            b=>!def.BoundarySegments.Any(s=>BilingualDrawingImporter.Crosses(b,s)) && !def.ProtectedGeometry.Any(g=>!g.Bounds.Contains(source.Source.Bounds) && BilingualDrawingImporter.Intersects(b,g.Bounds,0)));
                        var close=slots.Where(b=>BilingualLocalPlacement.Gap(source.Source.Bounds,b)<=source.Source.OriginalTextHeight*2).Take(32).ToArray();
                        var detail=new BilingualPlacementTrace();int accepted=0;
                        foreach(var slot in close){BilingualPlacementChecks.Move(text,fp,slot.Left,slot.Top,row.Geometry.InsertionPoint.Z);
                            if(BilingualPlacementChecks.Accept(text,slot,trace.Allowed,source,def,occupied,source.Source.OriginalTextHeight*.12,detail,out _))accepted++;}
                        trials.Add(new{sc,factor,width=text.Width,footprintWidth=fp.Width,footprintHeight=fp.Height,total=slots.Count,near=close.Length,accepted,rejections=detail.Counts,
                            first=detail.Examples.FirstOrDefault()});
                    }
                    results.Add(new{pair.SourceHandle,pair.TargetText,source.Source,placed,bounds=args[6],scale=args[7],normal=text.Normal,trials,trace=trace.Report(baseline,pairs,source.DefinitionName)});
                }
                File.WriteAllText(report,System.Text.Json.JsonSerializer.Serialize(new{status="passed",scope="Read-only local diagnostic; no drawing writes or delivery approval; background source MText uses captured saved bounds",results},CadTranslation.Contracts.JsonDefaults.Options));
            }
            finally{HostApplicationServices.WorkingDatabase=prior;}
        }
        catch(System.Exception ex){File.WriteAllText(report,System.Text.Json.JsonSerializer.Serialize(new{status="failed",error=ex.ToString()}));}
    }
    [CommandMethod("CAD_NEAR_LABEL_TEST")]
    public void NearLabel()
    {
        string report = Environment.GetEnvironmentVariable("CAD_CONTACT_TEST_REPORT")!;
        try
        {
            using var text = new MText(); text.SetDatabaseDefaults(); text.Attachment = AttachmentPoint.TopLeft;
            var box = new Rect2(0,0,10,3);
            const string contents = "Compressed Air and Nitrogen Station";
            var source = new CadLayoutText(ObjectId.Null,"near","model","1","AcDbText",contents,true,
                new TextLayoutSnapshot("near",box,box.Center,3),null);
            var definition = new CadDefinitionTopology("model","1",[],[],[source],[]);
            var occupied = new List<Rect2> { box, new(-100,-8,0,100), new(10,-8,100,100), new(0,3.5,10,100) };
            var method = typeof(BilingualDrawingImporter).GetMethod("Place",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
            object?[] args = {text,contents,source,definition,occupied,0d,box,0d,new BilingualPlacementTrace(),false};
            bool placed = (bool)method.Invoke(null,args)!;
            var result = (Rect2)args[6]!;
            Assert(placed && text.Text == contents && text.TextHeight >= 1.05 - 1e-9 &&
                result.Top <= -.35 && result.Top >= -3 && result.Left >= .35 && result.Right <= 9.65,
                $"use the readable wrapped pocket next to the source before a distant single line: placed={placed}, height={text.TextHeight}, bounds={result}");
            Assert(occupied.All(o=>!BilingualDrawingImporter.Intersects(result,o,.35)),"near text still needs full collision clearance");
            // A wide source caption can sit above a narrower free pocket.
            const string narrowContents = "Air and Gas Flow Control Room";
            occupied = new List<Rect2> { box, new(-100,-16,0,100), new(7.5,-16,100,0), new(10,0,100,100), new(0,3.5,10,100) };
            args = new object?[] {text,narrowContents,source,definition,occupied,0d,box,0d,new BilingualPlacementTrace(),false};
            placed = (bool)method.Invoke(null,args)!;
            var narrow = (Rect2)args[6]!;
            Assert(placed && narrow.Top >= -3 && narrow.Left >= .35 && narrow.Right <= 7.15 &&
                text.TextHeight >= 1.05-1e-9 && text.Text == narrowContents,
                $"wrap to the available pocket, not just the source caption width: {narrow}");
            Assert(occupied.All(o=>!BilingualDrawingImporter.Intersects(narrow,o,.35)),"narrow placement retains obstacle clearance");
            args = new object?[] {text,contents,source,definition,occupied,0d,box,0d,new BilingualPlacementTrace(),false};
            placed = (bool)method.Invoke(null,args)!;
            var condensed = (Rect2)args[6]!;
            Assert(placed && condensed.Top>=-3 && condensed.Left>=.35 && condensed.Right<=7.15 &&
                text.TextHeight>=1.05-1e-9 && text.Text==contents,
                $"try modest width fitting before moving an intact long word far away: {condensed}");
            var gridMethod=typeof(BilingualDrawingImporter).GetMethod("TryNearbyGrid",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
            var allowed=new Rect2(-20,-20,30,30);
            args=new object?[]{text,narrowContents,source,definition,allowed,occupied,0d,box,0d,new BilingualPlacementTrace()};
            placed=(bool)gridMethod.Invoke(null,args)!;
            var grid=(Rect2)args[7]!;
            Assert(placed && BilingualLocalPlacement.Gap(box,grid)<=12 && text.Text==narrowContents &&
                allowed.Contains(grid) && occupied.All(o=>!BilingualDrawingImporter.Intersects(grid,o,.35)),
                "bounded grid fallback must retain full content, proximity and clearance");
            args=new object?[]{text,narrowContents,source,definition,allowed,new List<Rect2>{allowed},0d,box,0d,new BilingualPlacementTrace()};
            Assert(!(bool)gridMethod.Invoke(null,args)!,"grid fallback must fail safely when no whitespace exists");
            File.WriteAllText(report,System.Text.Json.JsonSerializer.Serialize(new {status="passed",result,narrow,height=text.TextHeight}));
        }
        catch (System.Exception ex) { File.WriteAllText(report,System.Text.Json.JsonSerializer.Serialize(new {status="failed",error=ex.ToString()})); }
    }
    [CommandMethod("CAD_BILINGUAL_GROUP_TEST")]
    public void GroupDrawing()
    {
        string job=Environment.GetEnvironmentVariable("CAD_GROUP_SOURCE_JOB")!;
        string output=Environment.GetEnvironmentVariable("CAD_GROUP_TEST_OUTPUT")!;
        string report=Environment.GetEnvironmentVariable("CAD_CONTACT_TEST_REPORT")!;
        try
        {
            var rows=NativeDrawing.ReadRows<CadTranslation.Contracts.ManifestRecord>(Path.Combine(job,"exchange/manifest.input.jsonl"));
            var translations=NativeDrawing.ReadRows<CadTranslation.Contracts.TranslationRecord>(Path.Combine(job,"exchange/translations.output.jsonl")).ToDictionary(t=>t.RecordId);
            using var sourceDb=NativeDrawing.Open(Path.Combine(job,"working/source.dwg"));
            using var db=new Database(true,true);
            var mapping=new IdMapping();
            using(var read=sourceDb.TransactionManager.StartTransaction())
            using(var write=db.TransactionManager.StartTransaction())
            {
                var sourceBlocks=(BlockTable)read.GetObject(sourceDb.BlockTableId,OpenMode.ForRead);
                var model=(BlockTableRecord)read.GetObject(sourceBlocks[BlockTableRecord.ModelSpace],OpenMode.ForRead);
                Rect2[] windows=[new(26000,-40500,43000,-28000),new(44000,-74000,66000,-56000),new(13500,-99000,35500,-82000)];
                var ids=new ObjectIdCollection();
                var blockInfo=new List<object>();
                foreach(ObjectId id in model)
                {
                    var entity=(Entity)read.GetObject(id,OpenMode.ForRead);
                    try{var b=entity.GeometricExtents;var box=new Rect2(b.MinPoint.X,b.MinPoint.Y,b.MaxPoint.X,b.MaxPoint.Y);
                        if(entity is BlockReference br) blockInfo.Add(new{handle=br.Handle.ToString(),br.Name,bounds=box});
                        if(windows.Any(w=>BilingualDrawingImporter.Intersects(w,box,0))) ids.Add(id);}
                    catch(Autodesk.AutoCAD.Runtime.Exception ex){if(entity is BlockReference br){blockInfo.Add(new{handle=br.Handle.ToString(),br.Name,error=ex.Message});ids.Add(id);}}
                }
                File.WriteAllText(report+".blocks",System.Text.Json.JsonSerializer.Serialize(blockInfo,CadTranslation.Contracts.JsonDefaults.Options));
                var blocks=(BlockTable)write.GetObject(db.BlockTableId,OpenMode.ForRead);
                sourceDb.WblockCloneObjects(ids,blocks[BlockTableRecord.ModelSpace],mapping,DuplicateRecordCloning.Ignore,false);
                write.Commit();
            }
            var mapped=mapping.Cast<IdPair>().Where(p=>p.IsCloned).ToDictionary(p=>p.Key,p=>p.Value);
            var previous=HostApplicationServices.WorkingDatabase;
            try
            {
                HostApplicationServices.WorkingDatabase=db;
                using var tx=db.TransactionManager.StartTransaction();
                var access=new CadObjectAccess(db,tx);
                var inputs=rows.Where(r=>mapped.ContainsKey(sourceDb.GetObjectId(false,new Handle(Convert.ToInt64(r.Handle,16)),0)))
                    .Select(r=>new LayoutWriteInput(mapped[sourceDb.GetObjectId(false,new Handle(Convert.ToInt64(r.Handle,16)),0)],r,
                    TranslationValidator.RestoreProtectedTokensForOutput(translations[r.RecordId].TranslatedText,r.ProtectedTokens),false)).ToArray();
                File.WriteAllText(report+".stage","capture-topology");
                var baseline=DrawingTopologyCapture.Capture(db,tx,inputs,access);
                File.WriteAllText(report+".grid",System.Text.Json.JsonSerializer.Serialize(baseline.Definitions.Where(d=>d.Name=="*Model_Space").SelectMany(d=>d.BoundarySegments)
                    .Where(s=>s.MinX>18000 && s.MaxX<30000 && s.MinY> -93000 && s.MaxY< -85000),CadTranslation.Contracts.JsonDefaults.Options));
                var occupied=baseline.Definitions.ToDictionary(d=>d.Name,d=>d.Texts.Select(t=>t.Source.Bounds).ToList());
                foreach(var d in baseline.Definitions)
                foreach(var t in d.Texts)
                foreach(var name in occupied.Keys.Where(n=>n!=d.Name)) occupied[name].AddRange(InstanceOccupancyProjection.Project(t.Source.Bounds,d.Name,name,baseline.BlockInstances));
                var decisions=new List<object>();
                File.WriteAllText(report+".stage","group-plan");
                var slots=BilingualGroupLayout.Plan(db,baseline,inputs,occupied,"en",access,decisions);
                File.WriteAllText(report+".stage","save-groups");
                File.WriteAllText(report+".plan",System.Text.Json.JsonSerializer.Serialize(decisions,CadTranslation.Contracts.JsonDefaults.Options));
                var noteHandles=new[]{"A414","A415","A418","A430"};
                var noteRows=inputs.Where(i=>noteHandles.Contains(i.Manifest.Handle)).ToArray();
                Assert(noteRows.Length==4 && noteRows.All(i=>slots.ContainsKey(i.Manifest.RecordId)),"force definitions must be planned as a complete reading group");
                var noteSlots=noteRows.Select(i=>slots[i.Manifest.RecordId]).ToArray();
                Assert(noteSlots.All(s=>s.Strategy=="note-block") && noteSlots.Select(s=>s.Bounds.Left).Distinct().Count()==1 && noteSlots.Select(s=>s.Height).Distinct().Count()==1,"note alignment and height must be shared");
                var parameterSlots=slots.Values.Where(s=>s.Strategy=="table-aligned-block" && s.Bounds.Left>18000 && s.Bounds.Right<28000 && s.Bounds.Top< -91000 && s.Bounds.Bottom> -98000 && System.Text.RegularExpressions.Regex.IsMatch(s.DisplayText,@"^\d+\. ")).OrderByDescending(s=>s.Bounds.Top).ToArray();
                Assert(parameterSlots.Length==12 && parameterSlots.Select(s=>s.Bounds.Left).Distinct().Count()==1,"all twelve parameter rows must fit beneath the original table inside its frame");
                for(int i=0;i<12;i++)
                {
                    Assert(parameterSlots[i].DisplayText.StartsWith((i+1)+". "),"table row index association must survive grouping");
                    if(i>0) Assert(parameterSlots[i-1].Bounds.Bottom>parameterSlots[i].Bounds.Top,"group rows must not overlap");
                }
                var records=new List<object>();
                foreach(var input in inputs.Where(i=>slots.ContainsKey(i.Manifest.RecordId)))
                {
                    var slot=slots[input.Manifest.RecordId];
                    var original=(Entity)tx.GetObject(input.ObjectId,OpenMode.ForRead);
                    var owner=(BlockTableRecord)tx.GetObject(original.OwnerId,OpenMode.ForWrite);
                    var added=new MText(); added.SetDatabaseDefaults(db); added.Attachment=AttachmentPoint.TopLeft;
                    added.LayerId=original.LayerId; added.Color=original.Color;
                    added.Contents=BilingualGroupLayout.Contents(slot.DisplayText,"en");
                    added.TextHeight=slot.Height; added.Width=slot.WrapWidth;
                    var footprint=BilingualPlacementChecks.Footprint(added);
                    BilingualPlacementChecks.Move(added,footprint,slot.Bounds.Left,slot.Bounds.Top,0);
                    Assert(slot.Bounds.Contains(new Rect2(slot.Bounds.Left,slot.Bounds.Top-footprint.Height,slot.Bounds.Left+footprint.Width,slot.Bounds.Top),.001),"group measurement changed");
                    owner.AppendEntity(added); tx.AddNewlyCreatedDBObject(added,true);
                    records.Add(new{input.Manifest.Handle,target=added.Handle.ToString(),slot.Strategy,slot.Bounds,slot.Height});
                }
                tx.Commit(); NativeDrawing.Save(db,output);
                if(Environment.GetEnvironmentVariable("CAD_GROUP_PATCH_CANDIDATE") is {Length:>0} existingCandidate)
                {
                    Assert(!Path.GetFullPath(existingCandidate).Equals(Path.GetFullPath(output),StringComparison.OrdinalIgnoreCase),"local correction requires a separate output");
                    using var pairJson=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(job,"artifacts/bilingual-pairs.json")));
                    var oldPairs=pairJson.RootElement.GetProperty("pairs").EnumerateArray().ToDictionary(p=>p.GetProperty("recordId").GetString()!);
                    using var full=NativeDrawing.Open(existingCandidate);
                    HostApplicationServices.WorkingDatabase=full;
                    var expectedTargets=new Dictionary<string,string>();
                    using(var edit=full.TransactionManager.StartTransaction())
                    {
                        string[] forces=["A414","A415","A418","A430","6B4C","6B4D","6B50","6B68"];
                        var originalHandles=rows.Select(r=>r.Handle).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        foreach(var input in inputs.Where(i=>slots.ContainsKey(i.Manifest.RecordId)))
                        {
                            var point=input.Manifest.Geometry.InsertionPoint;
                            if(!forces.Contains(input.Manifest.Handle) && !(point.X>18000 && point.X<26000 && point.Y> -92000 && point.Y< -85000)) continue;
                            var old=oldPairs[input.Manifest.RecordId];
                            string handle=old.GetProperty("targetHandle").GetString()!;
                            Assert(old.GetProperty("decision").GetString()=="added" && !originalHandles.Contains(handle),"only generated bilingual targets may be corrected");
                            var target=(MText)edit.GetObject(full.GetObjectId(false,new Handle(Convert.ToInt64(handle,16)),0),OpenMode.ForWrite);
                            Assert(target.Text==old.GetProperty("targetText").GetString(),"candidate target content must match the selected task");
                            var slot=slots[input.Manifest.RecordId];
                            target.TextStyleId=full.Textstyle;target.Attachment=AttachmentPoint.TopLeft;target.Rotation=0;target.Normal=Vector3d.ZAxis;
                            target.Contents=BilingualGroupLayout.Contents(slot.DisplayText,"en");target.TextHeight=slot.Height;target.Width=slot.WrapWidth;
                            var footprint=BilingualPlacementChecks.Footprint(target);
                            BilingualPlacementChecks.Move(target,footprint,slot.Bounds.Left,slot.Bounds.Top,point.Z);
                            expectedTargets[handle]=slot.DisplayText;
                        }
                        edit.Commit();NativeDrawing.Save(full,output);
                    }
                    using var reopened=NativeDrawing.Open(output);
                    using var verify=reopened.TransactionManager.StartTransaction();
                    foreach(var item in expectedTargets)
                    {
                        var target=(MText)verify.GetObject(reopened.GetObjectId(false,new Handle(Convert.ToInt64(item.Key,16)),0),OpenMode.ForRead);
                        Assert(target.Text==item.Value,"saved grouped target lost content");
                    }
                    foreach(var row in rows.Where(r=>r.ObjectType is "AcDbText" or "AcDbMText"))
                    {
                        var entity=(Entity)verify.GetObject(reopened.GetObjectId(false,new Handle(Convert.ToInt64(row.Handle,16)),0),OpenMode.ForRead);
                        string raw=entity is MText m?m.Contents:((DBText)entity).TextString;
                        Assert(raw==row.RawText,"local correction changed an original text");
                    }
                    File.WriteAllText(report+".correction",System.Text.Json.JsonSerializer.Serialize(new{updatedTargets=expectedTargets.Count,sourceTextPreserved=true,scope="two force-note instances and the 12-row parameter table with heading; full structural gate remains pending"}));
                }
                File.WriteAllText(report,System.Text.Json.JsonSerializer.Serialize(new{status="generated",decisions,records},CadTranslation.Contracts.JsonDefaults.Options));
            }
            finally{HostApplicationServices.WorkingDatabase=previous;}
        }
        catch(System.Exception ex){File.WriteAllText(report,System.Text.Json.JsonSerializer.Serialize(new{status="failed",error=ex.ToString()}));}
    }

    [CommandMethod("CAD_BILINGUAL_REVIEW_TEST")]
    public void ReviewExisting() => NativeTestCommand.Execute(ReviewExistingCore);

    private static object ReviewExistingCore()
    {
        string job = Environment.GetEnvironmentVariable("CAD_LAYOUT_REVIEW_JOB") ?? "";
        if (string.IsNullOrWhiteSpace(job) || !Path.IsPathFullyQualified(job) || !Directory.Exists(job))
            throw new InvalidOperationException("CAD_LAYOUT_REVIEW_JOB must name an existing absolute job directory.");
        using var config=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(job,"config/export-job.json")));
        using var report=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(job,"artifacts/bilingual-pairs.json")));
        var pairs=System.Text.Json.JsonSerializer.Deserialize<BilingualDrawingImporter.Pair[]>(report.RootElement.GetProperty("pairs").GetRawText(),CadTranslation.Contracts.JsonDefaults.Options)!;
        // Rect2's constructor uses x1/y1/x2/y2, not serialized left/bottom/right/top.
        pairs=pairs.Select((p,i)=> {
            var b=report.RootElement.GetProperty("pairs")[i].GetProperty("bounds");
            return p with {Bounds=new Rect2(b.GetProperty("left").GetDouble(),b.GetProperty("bottom").GetDouble(),b.GetProperty("right").GetDouble(),b.GetProperty("top").GetDouble())};
        }).ToArray();
        var rows=NativeDrawing.ReadRows<CadTranslation.Contracts.ManifestRecord>(Path.Combine(job,"artifacts/bilingual-candidate.jsonl"));
        using var db=NativeDrawing.Open(config.RootElement.GetProperty("outputPath").GetString()!);
        var previous=HostApplicationServices.WorkingDatabase;
        try
        {
            HostApplicationServices.WorkingDatabase=db;
            var risks=BilingualSavedLayoutReview.MeasureAndInspect(db,pairs,rows,new Dictionary<string,Rect2>(),new List<CadObjectAccess.Issue>());
            return new {status="passed", risks,
                scope="Regression-only replay of saved text bounds; does not recheck original placement regions or approve delivery"};
        }
        finally { HostApplicationServices.WorkingDatabase=previous; }
    }

    [CommandMethod("CAD_CONTACT_TEST")]
    public void Run()
    {
        var report = Environment.GetEnvironmentVariable("CAD_CONTACT_TEST_REPORT")!;
        try
        {
            SavedBilingualReview();
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
            ObjectAccess();
            BilingualLocalSearch();
            BatchDiagnostic();
            ReadableNearby();
            BilingualCanCrossLinesButNotText();
            RotatedLocalSearch();
            using var wide = new Polyline();
            wide.AddVertexAt(0,new Point2d(0,0),0,4,4);
            wide.AddVertexAt(1,new Point2d(10,10),0,4,4);
            Assert(NativeGeometryContact.Intersects(wide,new Rect2(0,1,1,2)) is null,"wide polyline cannot be cleared by center-line testing");
            File.WriteAllText(report, "{\"status\":\"passed\",\"cases\":27}");
        }
        catch (System.Exception ex) { File.WriteAllText(report, System.Text.Json.JsonSerializer.Serialize(new {status="failed", error=ex.ToString()})); }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static void SavedBilingualReview()
    {
        CadTranslation.Contracts.ManifestRecord Row(string id,string handle,Rect2 box) => new("1.0",id,"hash","model",handle,"AcDbMText","Contents","text","Pump","Pump","Pump",[],
            new(new(0,0,0),null,0,new(new(box.Left,box.Bottom,0),new(box.Right,box.Top,0))),new("0","Standard",2,1,"","",new Dictionary<string,string>()),"input");
        var target=new Rect2(5,0,10,2);
        var pair=new BilingualDrawingImporter.Pair("a","AA","BB","Pump","added","model",target,.7);
        var rows=new[]{Row("a","AA",new Rect2(0,0,3,2)),Row("new","BB",target),Row("other","CC",new Rect2(8,0,12,2))};
        var risks=BilingualSavedLayoutReview.Inspect(new[]{pair,pair with {RecordId="follower",Decision="group-member"}},rows,
            new Dictionary<string,Rect2>{{"a",new Rect2(4,-1,9,3)}});
        Assert(risks.Any(r=>r.Code=="outside-placement-region" && r.TargetHandle=="BB") &&
            risks.Count(r=>r.Code=="saved-text-overlap" && r.OtherHandle=="CC")==1,
            "saved bilingual review must expose actual overflow and overlap, without double-counting term followers");
        Assert(!risks.Any(r=>r.Code=="distant-bilingual-label"), "a nearby label is not distant");
        var far = new Rect2(20,0,25,2);
        var farRows = new[]{rows[0],Row("new","BB",far)};
        var farPair = pair with { Bounds=far };
        Assert(BilingualSavedLayoutReview.Inspect(new[]{farPair},farRows,new Dictionary<string,Rect2>())
            .Any(r=>r.Code=="distant-bilingual-label"), "a remote individual label requires association review");
        Assert(!BilingualSavedLayoutReview.Inspect(new[]{farPair with {PlacementStrategy="table-copy"}},farRows,new Dictionary<string,Rect2>())
            .Any(r=>r.Code=="distant-bilingual-label"), "table copies are not individual nearby labels");
    }

    private static void RotatedLocalSearch()
    {
        using var text=new MText(); text.SetDatabaseDefaults(); text.Attachment=AttachmentPoint.TopLeft;
        foreach (double angle in new[] {Math.PI/2, -Math.PI/2, Math.PI/4, Math.PI})
        {
        text.Rotation=angle;
        var box=new Rect2(0,0,3,25);
        var source=new CadLayoutText(ObjectId.Null,"rot","model","AB","AcDbMText","Gearbox Centerline",true,
            new TextLayoutSnapshot("rot",box,box.Center,3),null);
        var definition=new CadDefinitionTopology("model","1",Array.Empty<Segment2>(),Array.Empty<LayoutRegion>(),
            new[]{source},Array.Empty<CadProtectedGeometry>());
        var allowed=new Rect2(-10,-5,20,35);
        var trace=new BilingualPlacementTrace();
        bool placed=BilingualDrawingImporter.TryLocalWhitespace(text,"Gearbox Centerline",source,definition,allowed,new[]{box},0,out var result,out _,trace);
        Assert(placed &&
            allowed.Contains(Box(text)) && !BilingualDrawingImporter.Intersects(Box(text),box,.36) &&
            result.Contains(Box(text),.01) && Math.Abs(Math.IEEERemainder(text.Rotation-angle,2*Math.PI))<1e-8,
            $"rotated local search must use the rotated footprint without changing orientation: placed={placed}, result={result}, actual={Box(text)}, rotation={text.Rotation}, rejected={System.Text.Json.JsonSerializer.Serialize(trace.Counts)}");
        }
    }

    private static void BilingualCanCrossLinesButNotText()
    {
        using var text=new MText(); text.SetDatabaseDefaults(); text.Attachment=AttachmentPoint.TopLeft;
        var box=new Rect2(0,0,3,3); var cell=new LayoutRegion("cell",LayoutRegionKind.TableCell,new Rect2(-1,-1,10,5));
        var source=new CadLayoutText(ObjectId.Null,"blocked","model","AB","AcDbText","Pump",true,
            new TextLayoutSnapshot("blocked",box,box.Center,3),cell);
        var lines=Enumerable.Range(0,61).Select(i=>new Segment2(new Point2(-1,-1+i*.1),new Point2(10,-1+i*.1))).ToArray();
        var definition=new CadDefinitionTopology("model","1",lines,new[]{cell},new[]{source},Array.Empty<CadProtectedGeometry>());
        var place=typeof(BilingualDrawingImporter).GetMethod("Place",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
        var trace=new BilingualPlacementTrace();
        object[] args={text,"Pump",source,definition,new List<Rect2>{box},0d,box,0d,trace,false};
        Assert((bool)place.Invoke(null,args)!,"Drawing lines must not block a bilingual label.");
        Assert(!BilingualDrawingImporter.Intersects((Rect2)args[6],box,.36),"A label must avoid original text even when crossing lines.");
        args[4]=new List<Rect2>{new(-1000,-1000,1000,1000)};
        Assert(!(bool)place.Invoke(null,args)!,"Real text occupancy must still block all placement candidates.");
        Assert(trace.Counts.GetValueOrDefault("text-overlap")>0 && trace.Examples.Count<=18,
            "Failed text clearance must retain bounded rejection evidence.");
    }

    private static void ReadableNearby()
    {
        using var text = new MText(); text.SetDatabaseDefaults(); text.Attachment=AttachmentPoint.TopLeft;
        var box=new Rect2(0,0,20,3);
        var source=new CadLayoutText(ObjectId.Null,"a","model","1","AcDbText","Water Pump",true,
            new TextLayoutSnapshot("a",box,box.Center,3),null);
        var definition=new CadDefinitionTopology("model","1",Array.Empty<Segment2>(),Array.Empty<LayoutRegion>(),
            new[]{source},Array.Empty<CadProtectedGeometry>());
        var place=typeof(BilingualDrawingImporter).GetMethod("Place",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
        object?[] args={text,"Water Pump",source,definition,new List<Rect2>{box},0d,box,0d,null,false};
        Assert((bool)place.Invoke(null,args)! && text.TextHeight>=2.1 && text.Text=="Water Pump" &&
            !BilingualDrawingImporter.Intersects((Rect2)args[6],box,.36),
            "open nearby space must retain a larger complete translation without touching source text");
    }

    private static void BatchDiagnostic()
    {
        string dir=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("CAD_CONTACT_TEST_REPORT"))!,"batch-diagnostic");
        Directory.CreateDirectory(dir);
        var config=new CadTranslation.Contracts.JobConfig("1.0","batch","import","","","hash","",null,"",
            Path.Combine(dir,"result.json"),dir,"zh-CN","en","bilingual");
        var context=(JobContext)Activator.CreateInstance(typeof(JobContext),System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic,
            null,new object[]{config,dir},null)!;
        var error=new CommandProtocolException("bilingual_invalid_batch","invalid batch");
        error.Data["validationErrors"]=new[] {
            new CadTranslation.Contracts.CommandError("invariant_mismatch","wrong number","r1","AB"),
            new CadTranslation.Contracts.CommandError("invariant_mismatch","wrong unit","r2","CD")};
        JobContext.TryWriteFailure("import",context,error);
        using var report=System.Text.Json.JsonDocument.Parse(File.ReadAllText(config.ResultPath));
        var errors=report.RootElement.GetProperty("errors");
        Assert(errors.GetArrayLength()==2 && errors[0].GetProperty("recordId").GetString()=="r1" &&
            errors[1].GetProperty("handle").GetString()=="CD", "batch failures must expose every failing record and handle without revalidating");
    }

    private static void BilingualLocalSearch()
    {
        using var text=new MText();
        text.SetDatabaseDefaults();
        text.Attachment=AttachmentPoint.TopLeft;
        var box=new Rect2(0,0,3,3);
        var source=new CadLayoutText(ObjectId.Null,"a","model","1","AcDbText","Inspection Window",true,
            new TextLayoutSnapshot("a",box,box.Center,3),null);
        var definition=new CadDefinitionTopology("model","1",Array.Empty<Segment2>(),Array.Empty<LayoutRegion>(),
            new[]{source},Array.Empty<CadProtectedGeometry>());
        var allowed=new Rect2(-20,-20,20,20);
        Assert(BilingualDrawingImporter.TryLocalWhitespace(text,"Inspection Window",source,definition,allowed,new[]{box},0,out var result,out _) &&
            allowed.Contains(result) && !BilingualDrawingImporter.Intersects(result,box,.36) && text.Text=="Inspection Window",
            "native local fallback must keep the complete readable target and respect the source obstacle");
        Assert(!BilingualDrawingImporter.TryLocalWhitespace(text,"Inspection Window",source,definition,allowed,new[]{allowed},0,out _,out _),
            "native local fallback must reject a fully occupied cell");
    }

    private static void ObjectAccess()
    {
        using var db=new Database(true,true);
        using var other=new Database(true,true);
        using var tx=db.TransactionManager.StartTransaction();
        var issues=new List<CadObjectAccess.Issue>();
        var access=new CadObjectAccess(db,tx,issues);
        Assert(access.Read<Entity>(ObjectId.Null,"optional",parentHandle:"P") is null && issues.Count==1,"null optional object must be recorded and skipped");
        try { access.Read<Entity>(ObjectId.Null,"source-read",block:"Model",parentHandle:"P",recordId:"r1",required:true); Assert(false,"required text must not be silently omitted"); }
        catch (CommandProtocolException ex)
        {
            Assert((string?)ex.Data["stage"]=="source-read" && (string?)ex.Data["recordId"]=="r1" && (string?)ex.Data["parentHandle"]=="P","required failure must retain diagnostic context");
            FailureDiagnostic(ex);
        }
        Assert(access.Read<DBObject>(other.BlockTableId,"foreign-db") is null && issues[^1].Error=="WrongDatabase","foreign database ID must not be read in the current transaction");
        var table=(BlockTable)tx.GetObject(db.BlockTableId,OpenMode.ForRead);
        var model=(BlockTableRecord)tx.GetObject(table[BlockTableRecord.ModelSpace],OpenMode.ForWrite);
        var line=new Line(Point3d.Origin,new Point3d(5,0,0));
        model.AppendEntity(line); tx.AddNewlyCreatedDBObject(line,true);
        Assert(access.Read<Entity>(line.ObjectId,"live")==line && line.EndPoint.X==5,"valid objects must remain readable and unchanged");
        line.Erase();
        Assert(access.Read<Entity>(line.ObjectId,"erased") is null,"erased object must be skipped before GetObject");
        var poly=new Polyline2d(); model.AppendEntity(poly); tx.AddNewlyCreatedDBObject(poly,true);
        var a=new Vertex2d(new Point3d(0,0,0),0,0,0,0); poly.AppendVertex(a); tx.AddNewlyCreatedDBObject(a,true);
        var b=new Vertex2d(new Point3d(8,8,0),0,0,0,0); poly.AppendVertex(b); tx.AddNewlyCreatedDBObject(b,true);
        Assert(!access.TryVertices(new[]{a.ObjectId,ObjectId.Null,b.ObjectId},"polyline",poly.Handle.ToString(),"Model",out var points) && points.Length==0,
            "missing vertex must invalidate the whole boundary, never bridge surviving vertices");
        Assert(access.TryVertices(new[]{a.ObjectId,b.ObjectId},"polyline",poly.Handle.ToString(),"Model",out points) && points.Length==2 && points[1]==b.Position,
            "complete vertex sequences must retain their original positions");
        table.UpgradeOpen();
        var definition=new BlockTableRecord {Name="AccessFixture"};
        table.Add(definition); tx.AddNewlyCreatedDBObject(definition,true);
        var reference=new BlockReference(new Point3d(3,4,0),definition.ObjectId);
        model.AppendEntity(reference); tx.AddNewlyCreatedDBObject(reference,true);
        var instances=BlockInstanceWalker.Capture(db,tx,access);
        Assert(instances.Any(i=>i.DefinitionId=="AccessFixture"),
            "protected block traversal must retain valid placed definitions");
    }

    private static void FailureDiagnostic(CommandProtocolException failure)
    {
        string dir=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("CAD_CONTACT_TEST_REPORT"))!,"diagnostic-case");
        Directory.CreateDirectory(dir);
        var config=new CadTranslation.Contracts.JobConfig("1.0","diagnostic","import","","","hash","",null,"",
            Path.Combine(dir,"result.json"),dir,"zh-CN","en","bilingual");
        var context=(JobContext)Activator.CreateInstance(typeof(JobContext),System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic,
            null,new object[]{config,dir},null)!;
        JobContext.TryWriteFailure("import",context,failure);
        using var report=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir,"command-diagnostic.json")));
        var data=report.RootElement;
        using var envelope=System.Text.Json.JsonDocument.Parse(File.ReadAllText(config.ResultPath));
        Assert(data.GetProperty("stage").GetString()=="source-read" && data.GetProperty("parentHandle").GetString()=="P" &&
            data.GetProperty("exception").GetString()!.Contains("CadObjectAccess") &&
            data.GetProperty("assemblySha256").GetString()==Hashing.Sha256File(typeof(JobContext).Assembly.Location) &&
            envelope.RootElement.GetProperty("errors")[0].GetProperty("recordId").GetString()=="r1",
            "failure artifact and result envelope must retain stage, context, stack and the actual runtime fingerprint");
    }

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

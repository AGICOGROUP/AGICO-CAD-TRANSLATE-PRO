using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using System.Security.Cryptography;
using System.Text.Json;
using App=Autodesk.AutoCAD.ApplicationServices.Core.Application;

// Integration fixture only: its extractor declares unsupported content explicitly.
public class NativeFixture {
 static string Root=>Environment.GetEnvironmentVariable("CAD_NATIVE_JOB")!;
 static JsonSerializerOptions Options=new(){WriteIndented=true};
 static string Hash(string path){using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();}
 static void Write(string name,object value){Directory.CreateDirectory(Root);File.WriteAllText(Path.Combine(Root,name),JsonSerializer.Serialize(value,Options));}
 static double[] P(Point3d p)=>new[]{p.X,p.Y,p.Z};
 static double[] Box(Entity e){try{var b=e.GeometricExtents;return new[]{b.MinPoint.X,b.MinPoint.Y,b.MaxPoint.X,b.MaxPoint.Y};}catch{return Array.Empty<double>();}}
 static object[] Views(Entity e,string block){var b=Box(e);if(b.Length!=4)return Array.Empty<object>();if(block=="*Model_Space")return new object[]{new{root="Model",instancePath=Array.Empty<string>(),box=b}};if(block=="FixtureNested"){var tr=e.Database.TransactionManager.TopTransaction;var owner=(BlockTableRecord)tr.GetObject(e.OwnerId,OpenMode.ForRead);var refs=owner.GetBlockReferenceIds(true,false);var views=new List<object>();foreach(ObjectId id in refs){var instance=(BlockReference)tr.GetObject(id,OpenMode.ForRead);var points=new[]{new Point3d(b[0],b[1],0),new Point3d(b[0],b[3],0),new Point3d(b[2],b[1],0),new Point3d(b[2],b[3],0)}.Select(p=>p.TransformBy(instance.BlockTransform)).ToArray();views.Add(new{root="Model",instancePath=new[]{instance.Handle.ToString()},box=new[]{points.Min(p=>p.X),points.Min(p=>p.Y),points.Max(p=>p.X),points.Max(p=>p.Y)}});}return views.ToArray();}return Array.Empty<object>();}
 static void Guard(string stage,Action action){try{action();}catch(System.Exception e){Write(stage+"-status.json",new{status="failed",errors=new[]{e.ToString()}});}}
 static ObjectId Style(Database db,Transaction tr,string name,string font){var styles=(TextStyleTable)tr.GetObject(db.TextStyleTableId,OpenMode.ForRead);if(styles.Has(name))return styles[name];styles.UpgradeOpen();var style=new TextStyleTableRecord{Name=name,FileName=font};var id=styles.Add(style);tr.AddNewlyCreatedDBObject(style,true);return id;}
 [CommandMethod("NFINIT")]
 public void Init()=>Guard("init",()=>{
  var db=App.DocumentManager.MdiActiveDocument.Database;
  using(var tr=db.TransactionManager.StartTransaction()){
   var style=Style(db,tr,"__fixtureCN",@"C:\Windows\Fonts\simhei.ttf");var bt=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForRead);var model=(BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace],OpenMode.ForWrite);
   void Text(BlockTableRecord owner,string text,double x,double y,double rotation=0){var entity=new DBText();entity.SetDatabaseDefaults(db);entity.TextStyleId=style;entity.TextString=text;entity.Height=2;entity.WidthFactor=1;entity.Position=new Point3d(x,y,0);entity.Rotation=rotation;owner.AppendEntity(entity);tr.AddNewlyCreatedDBObject(entity,true);entity.AdjustAlignment(db);}
   Text(model,"基础",0,10);Text(model,"柱",18,10,Math.PI/2);
   var note=new MText();note.SetDatabaseDefaults(db);note.TextStyleId=style;note.Location=new Point3d(0,-3,0);note.Width=32;note.TextHeight=2;note.Contents="基础采用C30混凝土，保护层50mm。";model.AppendEntity(note);tr.AddNewlyCreatedDBObject(note,true);
   var line=new Line(new Point3d(0,5,0),new Point3d(12,5,0));model.AppendEntity(line);tr.AddNewlyCreatedDBObject(line,true);
   bt.UpgradeOpen();var block=new BlockTableRecord{Name="FixtureNested"};bt.Add(block);tr.AddNewlyCreatedDBObject(block,true);Text(block,"梁",0,0);
   var reference=new BlockReference(new Point3d(25,0,0),block.ObjectId);model.AppendEntity(reference);tr.AddNewlyCreatedDBObject(reference,true);
   tr.Commit();
  }
  string path=Path.Combine(Root,"fixture-source.dwg");db.SaveAs(path,db.OriginalFileVersion);Write("init-status.json",new{status="created",path});
 });
 [CommandMethod("NFEXPORT")]
 public void Export(){string stage=Environment.GetEnvironmentVariable("CAD_NATIVE_STAGE")??"export";Guard(stage,()=>{
  var db=App.DocumentManager.MdiActiveDocument.Database;var rows=new List<object>();var geometry=new List<object>();var styles=new List<object>();var blocks=new List<object>();var layouts=new List<object>();var issues=new List<object>();
  using var tr=db.TransactionManager.StartTransaction();var bt=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForRead);var st=(TextStyleTable)tr.GetObject(db.TextStyleTableId,OpenMode.ForRead);
  foreach(ObjectId sid in st){var s=(TextStyleTableRecord)tr.GetObject(sid,OpenMode.ForRead);styles.Add(new{handle=s.Handle.ToString(),name=s.Name,font=s.FileName,bigFont=s.BigFontFileName,width=s.XScale,height=s.TextSize});}
  foreach(ObjectId bid in bt){var block=(BlockTableRecord)tr.GetObject(bid,OpenMode.ForRead);if(block.IsFromExternalReference){issues.Add(new{type="xref",block=block.Name});continue;}blocks.Add(new{name=block.Name,layout=block.IsLayout,origin=P(block.Origin)});
   foreach(ObjectId id in block){if(tr.GetObject(id,OpenMode.ForRead) is not Entity entity)continue;
    if(entity is DBText t){var s=(TextStyleTableRecord)tr.GetObject(t.TextStyleId,OpenMode.ForRead);rows.Add(new{handle=t.Handle.ToString(),kind="TEXT",slot="text",raw=t.TextString,visibleText=t.TextString,block=block.Name,layer=t.Layer,color=t.ColorIndex,style=s.Name,font=s.FileName,bigFont=s.BigFontFileName,position=P(t.Position),alignmentPoint=P(t.AlignmentPoint),height=t.Height,width=t.WidthFactor,widthFactor=t.WidthFactor,rotation=t.Rotation,box=Box(t),worldBoxes=Views(t,block.Name)});}
    else if(entity is MText m){var s=(TextStyleTableRecord)tr.GetObject(m.TextStyleId,OpenMode.ForRead);rows.Add(new{handle=m.Handle.ToString(),kind="MTEXT",slot="text",raw=m.Contents,visibleText=m.Text,block=block.Name,layer=m.Layer,color=m.ColorIndex,style=s.Name,font=s.FileName,bigFont=s.BigFontFileName,position=P(m.Location),height=m.TextHeight,width=m.Width,rotation=m.Rotation,box=Box(m),worldBoxes=Views(m,block.Name)});}
    else if(entity is Line l)geometry.Add(new{handle=l.Handle.ToString(),type="AcDbLine",block=block.Name,layer=l.Layer,shape=new{a=P(l.StartPoint),b=P(l.EndPoint)}});
    else if(entity is BlockReference b)geometry.Add(new{handle=b.Handle.ToString(),type="AcDbBlockReference",block=block.Name,layer=b.Layer,shape=new{point=P(b.Position),matrix=b.BlockTransform.ToArray(),definition=b.BlockTableRecord.Handle.ToString()}});
    else issues.Add(new{type="unsupported-fixture-entity",handle=entity.Handle.ToString(),kind=entity.GetRXClass().Name});
   }
  }
  var dictionary=(DBDictionary)tr.GetObject(db.LayoutDictionaryId,OpenMode.ForRead);foreach(DBDictionaryEntry item in dictionary){var layout=(Layout)tr.GetObject(item.Value,OpenMode.ForRead);layouts.Add(new{name=layout.LayoutName,model=layout.ModelType});}
  var snapshot=new{schemaVersion=1,tableIndexBase=0,worldBoxCoverage="complete",sourceSha256=Environment.GetEnvironmentVariable("CAD_NATIVE_SOURCE_HASH"),cadInputSha256=Environment.GetEnvironmentVariable("CAD_NATIVE_INPUT_HASH"),engine=new{version=App.Version,inputPath=Environment.GetEnvironmentVariable("CAD_NATIVE_INPUT")},rows,geometry,styles,blocks,layouts,coverageIssues=issues};
  string target=stage=="verify"?"saved/extracted.json":"extracted.json";Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Root,target))!);Write(target,snapshot);Write(stage+"-status.json",new{status="extracted",records=rows.Count});
 });}
 [CommandMethod("NFWRITE")]
 public void Apply()=>Guard("write",()=>{
  var db=App.DocumentManager.MdiActiveDocument.Database;using var map=JsonDocument.Parse(File.ReadAllText(Path.Combine(Root,"mapping.json")));var root=map.RootElement;
  if(Hash(Environment.GetEnvironmentVariable("CAD_NATIVE_INPUT")!)!=root.GetProperty("cadInputSha256").GetString())throw new InvalidOperationException("Input hash mismatch");
  var additions=new List<object>();int replaced=0,retained=0;
  using(var tr=db.TransactionManager.StartTransaction()){
   var style=Style(db,tr,"__fixtureEN",@"C:\Windows\Fonts\arial.ttf");
   foreach(var edit in root.GetProperty("edits").EnumerateArray()){
    string handle=edit.GetProperty("handle").GetString()!;var id=db.GetObjectId(false,new Handle(Convert.ToInt64(handle,16)),0);var e=(Entity)tr.GetObject(id,OpenMode.ForRead);string raw=e is DBText t?t.TextString:((MText)e).Contents;
    if(raw!=edit.GetProperty("expectedRaw").GetString())throw new InvalidOperationException("Raw mismatch");
    string action=edit.GetProperty("action").GetString()!;
    if(action=="retain"){retained++;continue;}
    if(action!="replace")throw new InvalidOperationException("Fixture supports replace and retain only");
    tr.GetObject(id,OpenMode.ForWrite,false,true);string target=edit.GetProperty("target").GetString()!;
    if(e is DBText text){text.TextString=target;text.TextStyleId=style;text.AdjustAlignment(db);}else{var note=(MText)e;note.Contents=target;note.TextStyleId=style;}
    replaced++;
   }
   var bt=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForRead);
   foreach(var add in root.GetProperty("additions").EnumerateArray()){
    var owner=(BlockTableRecord)tr.GetObject(bt[add.GetProperty("block").GetString()!],OpenMode.ForWrite);var properties=(Entity)tr.GetObject(db.GetObjectId(false,new Handle(Convert.ToInt64(add.GetProperty("properties").GetString(),16)),0),OpenMode.ForRead);var plan=add.GetProperty("placement");var p=plan.GetProperty("position").EnumerateArray().Select(v=>v.GetDouble()).ToArray();
    var target=new MText();target.SetDatabaseDefaults(db);target.SetPropertiesFrom(properties);target.TextStyleId=style;target.Location=new Point3d(p[0],p[1],p[2]);target.Attachment=AttachmentPoint.TopLeft;target.Width=plan.GetProperty("width").GetDouble();target.TextHeight=plan.GetProperty("height").GetDouble();target.Rotation=plan.GetProperty("rotation").GetDouble();target.Contents=add.GetProperty("target").GetString()!;owner.AppendEntity(target);tr.AddNewlyCreatedDBObject(target,true);
    additions.Add(new{id=add.GetProperty("id").GetString(),sourceKeys=add.GetProperty("sourceKeys").Clone(),handle=target.Handle.ToString(),slot="text"});
   }
   tr.Commit();
  }
  string path=Path.Combine(Root,"candidate.dwg");db.SaveAs(path,db.OriginalFileVersion);Write("write-status.json",new{schemaVersion=1,status="written-awaiting-verification",candidatePath=path,candidateSha256=Hash(path),counts=new{replaced,retained,added=additions.Count},errors=Array.Empty<string>(),additions,reflows=Array.Empty<object>()});
 });
}

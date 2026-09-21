using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CadTranslation.AutoCAD2025;
using CadTranslation.Contracts;
using CadTranslation.Core;
using System.Text.Json;

public sealed class WholeGroupPolicyTests
{
    [CommandMethod("CAD_WHOLE_GROUP_POLICY_TEST")]
    public void Run() => NativeTestCommand.Execute(() =>
    {
        var cases = new List<object>();
        var failures = new List<string>();
        foreach (string scenario in new[] { "prose", "roomy-table", "title-table", "paired-title", "polyline-grid", "single-column", "equipment-frames", "single-row-legend", "nested-weld", "already-bilingual", "partial-bilingual", "partial-preserved", "bottom-only", "rotated-cell", "neighbor-grid", "tall-left", "wide-bottom", "wide-bottom-drawing" })
        {
            try { cases.Add(Check(scenario)); }
            catch (System.Exception error) { failures.Add(scenario + ": " + error); }
        }
        return new { status = failures.Count == 0 ? "passed" : "failed", cases, failures };
    });

    private static object Check(string scenario)
    {
        using var db = new Database(true, true);
        var previous = HostApplicationServices.WorkingDatabase;
        try
        {
            HostApplicationServices.WorkingDatabase = db;
            using var tx = db.TransactionManager.StartTransaction();
            var owner = (BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
            if (scenario == "nested-weld")
            {
                var blockTable=(BlockTable)tx.GetObject(db.BlockTableId,OpenMode.ForWrite);
                owner=new BlockTableRecord { Name="WELD_DETAIL" };
                blockTable.Add(owner); tx.AddNewlyCreatedDBObject(owner,true);
            }
            var texts = new List<CadLayoutText>();
            var inputs = new List<LayoutWriteInput>();
            var regions = new List<LayoutRegion>();
            var segments = new List<Segment2>();
            void Add(Entity entity) { owner.AppendEntity(entity); tx.AddNewlyCreatedDBObject(entity, true); }
            void Text(Rect2 box, string raw, string target, bool rotated = false)
            {
                Entity entity;
                if (rotated) entity = new DBText { TextString = raw, Height = 3, Rotation = Math.PI / 2, Position = new(box.Left + 5, box.Bottom + 3, 0) };
                else entity = new MText { Contents = BilingualGroupLayout.Contents(raw, "zh-CN"), TextHeight = 3,
                    Width = 0, Attachment = AttachmentPoint.TopLeft, Location = new(box.Left + 3, box.Top - 3, 0) };
                Add(entity);
                var bounds = CadLayoutGeometry.TryLayoutBounds(entity)!;
                var actual = scenario == "prose" ? box : new Rect2(bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY);
                string id = entity.Handle.ToString();
                string contents = entity is MText mt ? mt.Contents : ((DBText)entity).TextString;
                var row = new ManifestRecord("1.0", id, "fixture", "model", id, entity is MText ? "AcDbMText" : "AcDbText", "Contents", "text", contents, raw, contents, [],
                    new(new(box.Left + 3, box.Top - 3, 0), null, rotated ? Math.PI / 2 : 0, null), new("0", "Standard", 3, 1, "", "", new Dictionary<string, string>()), id);
                inputs.Add(new(entity.ObjectId, row, target, true));
                texts.Add(new(entity.ObjectId, id, owner.Name, id, row.ObjectType, target, true, new(id, actual, actual.Center, 3), null));
            }
            if (scenario == "prose")
                Text(new(0, 0, 70, 30), string.Concat(Enumerable.Repeat("安装前请检查全部设备尺寸并确认基础强度符合图纸要求。", 5)),
                    string.Join(" ", Enumerable.Repeat("Check equipment dimensions and foundation strength before installation.", 9)));
            else
            {
                bool single = scenario is "single-column" or "equipment-frames";
                bool tall = scenario == "tall-left";
                int count = scenario == "equipment-frames" ? 2 : scenario == "single-row-legend" ? 3 : 4;
                for (int i = 0; i < count; i++)
                {
                    int x = single ? 0 : scenario == "single-row-legend" ? i : i % 2,
                        y = single ? i : scenario == "single-row-legend" ? 0 : i / 2;
                    int width = tall ? 25 : 70, height = tall ? 70 : 25;
                    var box = new Rect2(x * width, y * height, (x + 1) * width, (y + 1) * height);
                    regions.Add(new("cell", LayoutRegionKind.TableCell, box));
                    segments.AddRange([new(new(box.Left, box.Bottom), new(box.Right, box.Bottom)), new(new(box.Left, box.Top), new(box.Right, box.Top)),
                        new(new(box.Left, box.Bottom), new(box.Left, box.Top)), new(new(box.Right, box.Bottom), new(box.Right, box.Top))]);
                    bool bilingual = scenario == "already-bilingual" || scenario is "partial-bilingual" or "partial-preserved" && i == 0;
                    bool title = scenario is "title-table" or "paired-title" or "single-column";
                    string raw = scenario == "paired-title" ? new[] { "审定", "审校", "设计", "制图" }[i] :
                        title ? new[] { "日期", "签名", "实名", "专业" }[i] :
                        scenario is "single-row-legend" or "nested-weld" ? "单面角焊缝" : bilingual ? "水泵 Water Pump" : "水泵";
                    string target = scenario == "paired-title" ? new[] { "Approved by", "Checked by", "Designed by", "Drawn by" }[i] :
                        title ? new[] { "Date", "Signature", "Name", "Discipline" }[i] :
                        scenario is "single-row-legend" or "nested-weld" ? "Single-sided Fillet Weld" : "Water Pump";
                    if(scenario=="partial-preserved" && i==0)target=raw;
                    Text(box, raw, target, scenario == "single-column" || scenario == "rotated-cell" && i == 0);
                    if(scenario=="paired-title")
                    {
                        string existing=new[] { "APPROVED", "CHECKED", "DESIGNED", "DRAWN" }[i];
                        Text(new(box.Left,box.Bottom,box.Right,box.Top-8),existing,existing);
                    }
                }
                if(scenario=="neighbor-grid")
                    foreach(var b in new[]{new Rect2(150,0,190,25),new Rect2(150,25,190,50)})
                    {
                        regions.Add(new("neighbor-cell",LayoutRegionKind.TableCell,b));
                        segments.AddRange([new(new(b.Left,b.Bottom),new(b.Right,b.Bottom)),new(new(b.Left,b.Top),new(b.Right,b.Top)),
                            new(new(b.Left,b.Bottom),new(b.Left,b.Top)),new(new(b.Right,b.Bottom),new(b.Right,b.Top))]);
                    }
                if(scenario=="wide-bottom-drawing")
                {
                    segments.Add(new(new(5,60),new(135,100)));
                    segments.Add(new(new(5,100),new(135,60)));
                }
                if (scenario == "polyline-grid")
                {
                    foreach(var c in regions.Select(r=>r.Bounds))
                    {
                        var boundary=new Polyline();
                        boundary.AddVertexAt(0,new(c.Left,c.Bottom),0,0,0);
                        boundary.AddVertexAt(1,new(c.Right,c.Bottom),0,0,0);
                        boundary.AddVertexAt(2,new(c.Right,c.Top),0,0,0);
                        boundary.AddVertexAt(3,new(c.Left,c.Top),0,0,0);
                        boundary.Closed=true; Add(boundary);
                    }
                }
                else foreach (var s in segments.Distinct()) Add(new Line(new(s.Start.X, s.Start.Y, 0), new(s.End.X, s.End.Y, 0)));
                if(scenario=="tall-left")regions.Add(new("sheet",LayoutRegionKind.ClosedFrame,new(10,-10,200,200)));
                if(scenario is "wide-bottom" or "wide-bottom-drawing")
                {
                    var frame=new Rect2(-20,-10,160,200);
                    regions.Add(new("sheet",LayoutRegionKind.ClosedFrame,frame));
                    // The outside copy crosses the sheet border, which is not
                    // engineering artwork occupying that otherwise clear slot.
                    foreach(var edge in new[]{new Segment2(new(frame.Left,frame.Bottom),new(frame.Right,frame.Bottom)),
                        new Segment2(new(frame.Left,frame.Top),new(frame.Right,frame.Top)),
                        new Segment2(new(frame.Left,frame.Bottom),new(frame.Left,frame.Top)),
                        new Segment2(new(frame.Right,frame.Bottom),new(frame.Right,frame.Top))})
                    {
                        segments.Add(edge);
                        Add(new Line(new(edge.Start.X,edge.Start.Y,0),new(edge.End.X,edge.End.Y,0)));
                    }
                }
            }
            var baseline = new CadLayoutBaseline([new(owner.Name, owner.Handle.ToString(), segments, regions, texts, [])], []);
            var occupied = new Dictionary<string, List<Rect2>> { [owner.Name] = texts.Select(t => t.Source.Bounds).ToList() };
            if (scenario == "bottom-only") occupied[owner.Name].AddRange([new(-1000, 0, 0, 2000), new(140, 0, 2000, 2000), new(0, 50, 140, 2000)]);
            if (scenario == "neighbor-grid") occupied[owner.Name].AddRange([new(-1000,-1000,2000,-1),new(-1000,51,2000,1000)]);
            var decisions = new List<object>(); var blocked = new HashSet<string>();
            var slots = new Dictionary<string, BilingualGroupLayout.Slot>(); var ids = new HashSet<string>();
            var copies = new List<BilingualTableCopy.CopyReceipt>(); var pairs = new List<BilingualDrawingImporter.Pair>();
            if (scenario == "prose")
            {
                slots = BilingualGroupLayout.Plan(db, baseline, inputs.ToArray(), occupied, "en", new CadObjectAccess(db, tx), decisions, blocked: blocked);
                Require(slots.Count == 1 && blocked.Count == 0, "The whole prose block must be placed.");
                var slot = slots.Values.Single();
                Require(Math.Abs(slot.Bounds.Width - 70) < .001 && Math.Abs(slot.WrapWidth * CadLayoutGeometry.MTextMeasurementSafetyScale - 70) < .001,
                    "Prose must retain the original block width.");
                Require(slot.Height == 3 && slot.Bounds.Top == 30 && (slot.Bounds.Left >= 70 || slot.Bounds.Right <= 0), "Use readable full-height text on the left/right, aligned to the top.");
            }
            else
            {
                var handled = BilingualTableCopy.Apply(db, tx, baseline, inputs.ToArray(), occupied, "en", pairs, copies, decisions, slots, ids, blocked);
                if (scenario is "single-column" or "equipment-frames")
                    Require(ids.Count == 0 && blocked.Count == 0 && copies.Count == 0, "Single-column labels must remain eligible for local translation.");
                else if (scenario == "single-row-legend")
                {
                    Require(ids.Count == 0 && blocked.Count == 0 && copies.Count == 0,
                        "A single row of welding labels is not a complete table.");
                    BilingualGroupLayout.Plan(db,baseline,inputs.ToArray(),occupied,"en",new CadObjectAccess(db,tx),decisions,blocked:blocked);
                    Require(blocked.Count == 0,"Single-row welding labels must remain eligible for local placement.");
                }
                else if (scenario == "nested-weld")
                {
                    Require(ids.Count == 0 && blocked.Count == 0 && copies.Count == 0,
                        "Nested diagram blocks do not enter the complete-table copy path.");
                    BilingualGroupLayout.Plan(db,baseline,inputs.ToArray(),occupied,"en",new CadObjectAccess(db,tx),decisions,blocked:blocked);
                    Require(blocked.Count == 0,"Nested weld labels must remain eligible for local placement.");
                }
                else if (scenario == "already-bilingual")
                    Require(copies.Count == 0 && slots.Count == 0 && blocked.Count == 0, "Complete bilingual tables must not be duplicated.");
                else if (scenario == "partial-preserved")
                    Require(copies.Count == 0 && blocked.Count>0, "A preserved bilingual string is not a target-only copy translation; request the missing target instead of delivering a mixed-language copy.");
                else
                {
                    Require(handled.Count == 4 && slots.Count == 0 && blocked.Count == 0 && copies.Count > 4, "Every table must be copied with its grid and all target cells: " + JsonSerializer.Serialize(decisions));
                    if(scenario=="paired-title")
                    {
                        var textHandles=texts.Select(t=>t.EntityHandle).ToHashSet();
                        Require(copies.Count(c=>textHandles.Contains(c.SourceHandle))==4,
                            "A copied title cell must contain one English label, not the old and new wording together: "+
                            JsonSerializer.Serialize(new { copiedText=copies.Where(c=>textHandles.Contains(c.SourceHandle)),
                                sourceTexts=texts.Select(t=>new { t.EntityHandle,t.CandidateText }) }));
                    }
                    Require(copies.All(c => (c.Dx == 0) != (c.Dy == 0)), "Align the copy on one axis.");
                    bool expected = scenario switch {
                        "tall-left" => copies.All(c=>c.Dx>0 && c.Dy==0),
                        "wide-bottom" => copies.All(c=>c.Dx==0 && c.Dy>0),
                        "wide-bottom-drawing" => copies.All(c=>c.Dx==0 && c.Dy<0),
                        "neighbor-grid" => copies.All(c=>c.Dx<0 && c.Dy==0),
                        _ => copies.All(c=>c.Dx==0 && c.Dy<0)
                    };
                    Require(expected,"Keep whole copied tables adjacent and choose the inward shape axis before other positions.");
                    foreach (var p in pairs)
                    {
                        var entity = (Entity)tx.GetObject(db.GetObjectId(false, new Handle(Convert.ToInt64(p.TargetHandle, 16)), 0), OpenMode.ForRead);
                        string target = entity is MText mt ? mt.Text : ((DBText)entity).TextString;
                        Require(target == p.TargetText, "Copy must contain complete target-only text.");
                    }
                }
            }
            foreach (var input in inputs)
            {
                var entity = (Entity)tx.GetObject(input.ObjectId, OpenMode.ForRead);
                string original = entity is MText mt ? mt.Contents : ((DBText)entity).TextString;
                Require(original == input.Manifest.RawText, "Original text must remain unchanged.");
            }
            tx.Commit();
            string output = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("CAD_CONTACT_TEST_REPORT"))!, scenario + ".dwg");
            NativeDrawing.Save(db, output);
            return new { scenario, slots = slots.Count, copies = copies.Count, output, decisions };
        }
        finally { HostApplicationServices.WorkingDatabase = previous; }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

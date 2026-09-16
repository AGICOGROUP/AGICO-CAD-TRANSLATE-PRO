using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

// Bilingual-only: prefer readable suffixes in roomy cells; otherwise copy the full grid nearby.
internal static class BilingualTableCopy
{
    internal sealed record CopyReceipt(string SourceHandle, string TargetHandle, double Dx, double Dy, string? TargetText, Rect2 Table);

    internal static HashSet<string> Apply(Database db, Transaction tx, CadLayoutBaseline baseline,
        LayoutWriteInput[] inputs, Dictionary<string, List<Rect2>> occupied, string language,
        List<BilingualDrawingImporter.Pair> pairs, List<CopyReceipt> receipts, List<object> decisions,
        Dictionary<string, BilingualGroupLayout.Slot> inlineSlots, HashSet<string> tableIds, HashSet<string> blocked,
        CadObjectAccess? access = null)
    {
        access ??= new CadObjectAccess(db, tx);
        var requiredIds = inputs.Select(i => i.ObjectId).ToHashSet();
        var handled = new HashSet<string>();
        var byId = inputs.ToDictionary(i => i.Manifest.RecordId);
        var changed = inputs.Where(i => BilingualDrawingImporter.Plain(i.RestoredText) != BilingualDrawingImporter.Plain(i.Manifest.RawText))
            .ToDictionary(i => i.Manifest.RecordId);
        foreach (var definition in baseline.Definitions)
        foreach (var text in definition.Texts)
        {
            var entity = access.Read<Entity>(text.ObjectId, "table-pair-scan", definition.Name,
                recordId: text.RecordId, required: requiredIds.Contains(text.ObjectId));
            if (entity is null) continue;
            if (entity.ExtensionDictionary.IsNull) continue;
            var dict = access.Read<DBDictionary>(entity.ExtensionDictionary, "table-pair-dictionary", definition.Name, entity.Handle.ToString());
            if (dict is null) continue;
            if (!dict.Contains("CADTRANS_TABLE_PAIR")) continue;
            var data = access.Read<Xrecord>(dict.GetAt("CADTRANS_TABLE_PAIR"), "table-pair-record", definition.Name, entity.Handle.ToString())?.Data?.AsArray();
            if (data is not { Length: 2 } || data[0].Value is not string || data[1].Value is not string) continue;
            var source = inputs.FirstOrDefault(i => i.Manifest.Handle == (string)data[0].Value);
            if (source is null || !changed.ContainsKey(source.Manifest.RecordId)) continue;
            string expected = BilingualDrawingImporter.Plain(source.RestoredText);
            string actual = entity is MText m ? m.Text : entity is DBText d ? d.TextString : "";
            if (actual != expected || (string)data[1].Value != expected) continue;
            handled.Add(source.Manifest.RecordId);
            pairs.Add(new(source.Manifest.RecordId,source.Manifest.Handle,entity.Handle.ToString(),expected,"existing-table-copy",definition.Name,text.Source.Bounds,1,"table-copy"));
        }
        foreach (var definition in baseline.Definitions)
        {
            // Definitions instantiated multiple times need instance-specific placement, not one shared copy.
            if (!definition.Name.StartsWith("*Model_Space", StringComparison.OrdinalIgnoreCase) &&
                !definition.Name.StartsWith("*Paper_Space", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var cells in BilingualTableLayout.TranslationGroups(definition.Regions,definition.BoundarySegments))
            {
                if (cells.Length < 3 || cells.Select(c => Math.Round(c.Center.Y, 3)).Distinct().Count() < 2) continue;
                var table = new Rect2(cells.Min(c => c.Left), cells.Min(c => c.Bottom), cells.Max(c => c.Right), cells.Max(c => c.Top));
                var sources = definition.Texts.Where(t => byId.ContainsKey(t.RecordId) &&
                    cells.Any(c => c.Contains(new Rect2(t.Source.Bounds.Center.X,t.Source.Bounds.Center.Y,t.Source.Bounds.Center.X,t.Source.Bounds.Center.Y)))).ToArray();
                var requests = sources.Where(t => changed.ContainsKey(t.RecordId) && !handled.Contains(t.RecordId)).ToArray();
                if (BilingualTitlePanel.IsTitlePanel(sources.Select(t => BilingualDrawingImporter.Plain(byId[t.RecordId].Manifest.RawText))))
                    continue; // Keep title fields paired locally; do not copy or collect them as a schedule.
                if (requests.Length == 0) continue;
                Rect2 Cell(CadLayoutText t) => cells.Where(c => c.Contains(new Rect2(t.Source.Bounds.Center.X,t.Source.Bounds.Center.Y,t.Source.Bounds.Center.X,t.Source.Bounds.Center.Y)))
                    .OrderBy(c => c.Area).First();
                var reuse = new Dictionary<string,CadLayoutText>();
                foreach(var t in requests)
                {
                    string target=BilingualDrawingImporter.Plain(changed[t.RecordId].RestoredText);
                    var neighbors=sources.Where(o=>o.RecordId!=t.RecordId && Cell(o)==Cell(t) &&
                        BilingualLabelEquivalence.MatchesNeighbor(byId[t.RecordId].Manifest.RawText,target,
                            BilingualDrawingImporter.Plain(byId[o.RecordId].Manifest.RawText))).ToArray();
                    if(neighbors.Length==1) reuse[t.RecordId]=neighbors[0];
                }
                var pending=requests.Where(t=>!reuse.ContainsKey(t.RecordId) &&
                    !(language.StartsWith("en",StringComparison.OrdinalIgnoreCase) && BilingualFixedLabelPolicy.ContainsEmbeddedEnglish(byId[t.RecordId].Manifest.RawText))).ToArray();
                foreach(var t in sources) tableIds.Add(t.RecordId);
                var proposed=new Dictionary<string,BilingualGroupLayout.Slot>();
                var obstacles=new List<Rect2>(occupied[definition.Name]);
                foreach(var t in pending)
                {
                    if(Math.Abs(byId[t.RecordId].Manifest.Geometry.RotationRadians)>1e-6) break;
                    bool fitsInline=false;
                    foreach(double scale in new[]{1.0,.85,.7})
                    {
                        using var probe=new MText(); probe.SetDatabaseDefaults(db); probe.Attachment=AttachmentPoint.TopLeft;
                        probe.TextStyleId=db.Textstyle; probe.TextHeight=t.Source.OriginalTextHeight*scale; probe.Width=0;
                        string target=BilingualDrawingImporter.Plain(changed[t.RecordId].RestoredText);
                        probe.Contents=BilingualGroupLayout.Contents(target,language);
                        var fp=BilingualPlacementChecks.Footprint(probe);
                        var candidate=BilingualTablePlacement.AfterSource(Cell(t),t.Source.Bounds,fp.Width,fp.Height,t.Source.OriginalTextHeight,obstacles);
                        if(candidate is not Rect2 slot) continue;
                        BilingualPlacementChecks.Move(probe,fp,slot.Left,slot.Top,byId[t.RecordId].Manifest.Geometry.InsertionPoint.Z);
                        if(!BilingualPlacementChecks.Accept(probe,slot,Cell(t),t,definition,obstacles,t.Source.OriginalTextHeight*.12,null,out var actual)) continue;
                        var reserved=new Rect2(Math.Min(slot.Left,actual.Left),Math.Min(slot.Bottom,actual.Bottom),Math.Max(slot.Right,actual.Right),Math.Max(slot.Top,actual.Top));
                        proposed[t.RecordId]=new(reserved,probe.TextHeight,0,"table-inline-right",target);
                        obstacles.Add(reserved); fitsInline=true; break;
                    }
                    if(!fitsInline) break;
                }
                if(proposed.Count==pending.Length)
                {
                    foreach(var p in proposed)
                    {
                        inlineSlots[p.Key]=p.Value; occupied[definition.Name].Add(p.Value.Bounds);
                        foreach(var name in occupied.Keys.Where(n=>n!=definition.Name))
                            occupied[name].AddRange(InstanceOccupancyProjection.Project(p.Value.Bounds,definition.Name,name,baseline.BlockInstances));
                    }
                    decisions.Add(new{table,strategy="table-inline-right",recordIds=pending.Select(t=>t.RecordId).ToArray(),reused=reuse.Count});
                    continue;
                }
                // A failed copy stays a grouped repair; it must not scatter into tiny labels.
                foreach(var t in pending) blocked.Add(t.RecordId);
                var owner = access.Read<BlockTableRecord>(db.GetObjectId(false, new Handle(Convert.ToInt64(definition.Handle, 16)), 0), "table-owner", definition.Name);
                if (owner is null) { decisions.Add(new { table, strategy="table-copy-unresolved", reason="unreadable-table-owner" }); continue; }
                var members = new List<Entity>();
                bool unsupported = false;
                foreach (ObjectId id in owner)
                {
                    var entity = access.Read<Entity>(id, "table-member", definition.Name, owner.Handle.ToString());
                    if (entity is null) { unsupported = true; break; }
                    if (entity is Line line) { if (!Clip(line,table,out _,out _)) continue; }
                    else if (Bounds(entity) is not { } b || !table.Contains(b,1e-3)) continue;
                    if (entity is not Line and not MText and not DBText) { unsupported = true; break; }
                    members.Add(entity);
                }
                if (unsupported || !members.OfType<Line>().Any() || requests.Any(t => !members.Any(m=>m.ObjectId==t.ObjectId) || Math.Abs(changed[t.RecordId].Manifest.Geometry.RotationRadians) > 1e-6))
                { decisions.Add(new { table, strategy = "table-copy-unresolved", reason = "unsupported-table-members-or-rotation" }); continue; }

                double gap = cells.Select(c=>c.Height).Order().ElementAt(cells.Length/2)*.25;
                var offsets = BilingualTablePlacement.AdjacentCopies(table,gap)
                    .Select(b=>new Vector3d(b.Left-table.Left,b.Bottom-table.Bottom,0)).ToArray();
                var detectedFrames = DetectFrames(definition.BoundarySegments, table);
                var frames = definition.Regions.Concat(detectedFrames.Select(b => new LayoutRegion("table-copy-frame",LayoutRegionKind.ClosedFrame,b))).Where(r => r.Bounds.Contains(table) && r.Bounds.Area > table.Area * 1.5)
                    .OrderBy(r => r.Bounds.Area).ToArray();
                Vector3d? selected = null;
                foreach (var offset in offsets)
                {
                    Rect2 destination = Move(table, offset);
                    if (occupied[definition.Name].Any(b => Overlap(destination, b, gap*.1))) continue;
                    if (definition.BoundarySegments.Any(s => Overlap(destination, new Rect2(s.MinX,s.MinY,s.MaxX,s.MaxY), gap*.1))) continue;
                    if (definition.ProtectedGeometry.Any(g => !g.Bounds.Contains(destination) && Overlap(destination,g.Bounds,gap*.1))) continue;
                    selected = offset; break;
                }
                if (selected is not { } delta)
                { decisions.Add(new { table, strategy = "table-copy-unresolved", reason = "no-immediately-adjacent-space", frames = frames.Select(f => f.Bounds).ToArray(), candidates = offsets.Select(o => new { destination = Move(table,o), text = occupied[definition.Name].Count(b => Overlap(Move(table,o),b,gap*.1)), lines = definition.BoundarySegments.Where(s => Overlap(Move(table,o),new Rect2(s.MinX,s.MinY,s.MaxX,s.MaxY),gap*.1)).ToArray(), geometry = definition.ProtectedGeometry.Count(g => !g.Bounds.Contains(Move(table,o)) && Overlap(Move(table,o),g.Bounds,gap*.1)) }).ToArray() }); continue; }

                var staged = new List<(Entity Source, Entity Clone, CadLayoutText? Text, string? Target)>();
                bool fits = true;
                string? failedHandle = null;
                object? failedDetails = null;
                foreach (var member in members)
                {
                    // In a target-only copy, one existing equivalent target replaces the paired source label.
                    if(sources.Any(t=>t.ObjectId==member.ObjectId && reuse.ContainsKey(t.RecordId))) continue;
                    var text = sources.FirstOrDefault(t => t.ObjectId == member.ObjectId && changed.ContainsKey(t.RecordId));
                    var alias=reuse.FirstOrDefault(p=>p.Value.ObjectId==member.ObjectId);
                    if(alias.Key is not null) text=sources.First(t=>t.RecordId==alias.Key);
                    Entity clone = (Entity)member.Clone();
                    if (member is Line originalLine && clone is Line clonedLine && Clip(originalLine,table,out var start,out var end))
                    { clonedLine.StartPoint=start; clonedLine.EndPoint=end; }
                    string? target = null;
                    if (text is not null)
                    {
                        target = BilingualDrawingImporter.Plain(changed[text.RecordId].RestoredText);
                        var cell = Cell(text);
                        if (cell.Area <= 0 || !Fit(clone, target, cell, language)) { failedHandle=member.Handle.ToString(); failedDetails=new { cell, source=text.Source.Bounds, candidate=Bounds(clone), target }; clone.Dispose(); fits = false; break; }
                    }
                    clone.TransformBy(Matrix3d.Displacement(delta));
                    staged.Add((member, clone, text, target));
                }
                if (!fits)
                {
                    foreach (var item in staged) item.Clone.Dispose();
                    decisions.Add(new { table, strategy = "table-copy-unresolved", reason = "copy-text-does-not-fit", failedHandle, failedDetails }); continue;
                }
                owner.UpgradeOpen();
                foreach (var item in staged)
                {
                    owner.AppendEntity(item.Clone); tx.AddNewlyCreatedDBObject(item.Clone, true);
                    receipts.Add(new(item.Source.Handle.ToString(),item.Clone.Handle.ToString(),delta.X,delta.Y,item.Target,table));
                    if (item.Text is { } t && item.Target is { } target)
                    {
                        handled.Add(t.RecordId);
                        blocked.Remove(t.RecordId);
                        var b = Bounds(item.Clone)!.Value;
                        double height = item.Clone is MText mt ? mt.TextHeight : ((DBText)item.Clone).Height;
                        pairs.Add(new(t.RecordId,t.EntityHandle,item.Clone.Handle.ToString(),target,"added",definition.Name,b,height/t.Source.OriginalTextHeight,"table-copy"));
                        item.Clone.CreateExtensionDictionary();
                        var dictionary=(DBDictionary)tx.GetObject(item.Clone.ExtensionDictionary,OpenMode.ForWrite);
                        var link=new Xrecord { Data=new ResultBuffer(new TypedValue(1,t.EntityHandle),new TypedValue(1,target)) };
                        dictionary.SetAt("CADTRANS_TABLE_PAIR",link); tx.AddNewlyCreatedDBObject(link,true);
                    }
                }
                var envelope = Move(table,delta);
                occupied[definition.Name].Add(envelope);
                foreach (var name in occupied.Keys.Where(n => n != definition.Name))
                    occupied[name].AddRange(InstanceOccupancyProjection.Project(envelope,definition.Name,name,baseline.BlockInstances));
                decisions.Add(new { table, destination = envelope, strategy = "table-copy", reason = "insufficient-cell-space", gap,
                    outsideContainingFrame=frames.Length>0 && !frames.Any(f=>f.Bounds.Contains(envelope)),
                    copiedEntities = staged.Count, translatedCells = staged.Count(s => s.Target != null) });
            }
        }
        return handled;
    }

    private static bool Fit(Entity entity, string target, Rect2 cell, string language)
    {
        double originalHeight = entity is MText mt ? mt.TextHeight : ((DBText)entity).Height;
        var inner = new Rect2(cell.Left+originalHeight*.15,cell.Bottom+originalHeight*.15,cell.Right-originalHeight*.15,cell.Top-originalHeight*.15);
        foreach (double scale in new[] {1.0,.9,.8,.7,.65})
        {
            if (entity is MText text)
            {
                text.Contents = (language.StartsWith("zh",StringComparison.OrdinalIgnoreCase) ? @"\FSimSun;" : "") + target.Replace("\\","\\\\").Replace("{","\\{").Replace("}","\\}");
                text.TextHeight = originalHeight*scale;
                text.Width = 0;
                if (text.ActualWidth > inner.Width) text.Width = inner.Width;
            }
            else { var text2=(DBText)entity; text2.TextString=target; text2.Height=originalHeight*scale; }
            if (Bounds(entity) is not { } b || b.Width > inner.Width || b.Height > inner.Height) continue;
            // Keep the cloned source anchor if it fits; otherwise translate only within its cell.
            double dx = b.Left < inner.Left ? inner.Left-b.Left : b.Right > inner.Right ? inner.Right-b.Right : 0;
            double dy = b.Bottom < inner.Bottom ? inner.Bottom-b.Bottom : b.Top > inner.Top ? inner.Top-b.Top : 0;
            entity.TransformBy(Matrix3d.Displacement(new Vector3d(dx,dy,0)));
            return Bounds(entity) is { } final && inner.Contains(final,1e-3);
        }
        return false;
    }

    internal static void Verify(JobContext context, IReadOnlyList<CopyReceipt> copies)
    {
        if (copies.Count == 0) return;
        using var db = NativeDrawing.Open(context.Config.OutputPath);
        using var tx = db.TransactionManager.StartTransaction();
        foreach (var copy in copies)
        {
            Entity Get(string h) => (Entity)tx.GetObject(db.GetObjectId(false,new Handle(Convert.ToInt64(h,16)),0),OpenMode.ForRead);
            var source = Get(copy.SourceHandle); var target = Get(copy.TargetHandle);
            bool valid = source.GetRXClass().Name == target.GetRXClass().Name && source.Layer == target.Layer &&
                source.Color == target.Color && source.LinetypeId == target.LinetypeId && source.LineWeight == target.LineWeight;
            if (source is Line a && target is Line b)
            {
                var delta = new Vector3d(copy.Dx,copy.Dy,0);
                valid &= Clip(a,copy.Table,out var start,out var end) && (start+delta).DistanceTo(b.StartPoint)<1e-5 && (end+delta).DistanceTo(b.EndPoint)<1e-5;
            }
            else
            {
                string Read(Entity e) => e is MText m ? m.Text : ((DBText)e).TextString;
                valid &= Read(target) == (copy.TargetText ?? Read(source));
                if (copy.TargetText is null && Bounds(source) is { } s && Bounds(target) is { } t)
                    valid &= Math.Abs(t.Left-s.Left-copy.Dx)<1e-3 && Math.Abs(t.Bottom-s.Bottom-copy.Dy)<1e-3;
            }
            if (!valid) throw new CommandProtocolException("bilingual_table_copy_mismatch",$"Invalid table copy {copy.SourceHandle} -> {copy.TargetHandle}");
        }
    }

    private static bool Clip(Line line,Rect2 table,out Point3d start,out Point3d end)
    {
        start=line.StartPoint; end=line.EndPoint;
        if (Math.Abs(start.Y-end.Y)<1e-3 && start.Y>=table.Bottom-1e-3 && start.Y<=table.Top+1e-3)
        {
            double left=Math.Max(table.Left,Math.Min(start.X,end.X)),right=Math.Min(table.Right,Math.Max(start.X,end.X));
            if(right-left<=1e-3) return false;
            bool forward=start.X<end.X; start=new Point3d(forward?left:right,start.Y,start.Z); end=new Point3d(forward?right:left,end.Y,end.Z); return true;
        }
        if (Math.Abs(start.X-end.X)<1e-3 && start.X>=table.Left-1e-3 && start.X<=table.Right+1e-3)
        {
            double bottom=Math.Max(table.Bottom,Math.Min(start.Y,end.Y)),top=Math.Min(table.Top,Math.Max(start.Y,end.Y));
            if(top-bottom<=1e-3) return false;
            bool forward=start.Y<end.Y; start=new Point3d(start.X,forward?bottom:top,start.Z); end=new Point3d(end.X,forward?top:bottom,end.Z); return true;
        }
        return false;
    }

    private static IEnumerable<Rect2> DetectFrames(IReadOnlyList<Segment2> segments, Rect2 table)
    {
        var horizontal = segments.Where(s => s.IsHorizontal(1e-3) && s.MinX <= table.Left+1e-3 && s.MaxX >= table.Right-1e-3).ToArray();
        foreach (var bottom in horizontal.Where(s => s.Start.Y <= table.Bottom+1e-3))
        foreach (var top in horizontal.Where(s => s.Start.Y >= table.Top-1e-3))
        {
            if (Math.Abs(bottom.MinX-top.MinX)>1e-3 || Math.Abs(bottom.MaxX-top.MaxX)>1e-3) continue;
            bool Edge(double x) => segments.Any(s => s.IsVertical(1e-3) && Math.Abs(s.Start.X-x)<1e-3 && s.MinY<=bottom.Start.Y+1e-3 && s.MaxY>=top.Start.Y-1e-3);
            if (Edge(bottom.MinX) && Edge(bottom.MaxX)) yield return new Rect2(bottom.MinX,bottom.Start.Y,bottom.MaxX,top.Start.Y);
        }
    }

    private static Rect2 Move(Rect2 r, Vector3d d) => new(r.Left+d.X,r.Bottom+d.Y,r.Right+d.X,r.Top+d.Y);
    private static bool Overlap(Rect2 a,Rect2 b,double gap) => a.Left < b.Right+gap && a.Right > b.Left-gap && a.Bottom < b.Top+gap && a.Top > b.Bottom-gap;
    private static Rect2? Bounds(Entity e)
    {
        try { var b = e is MText mt ? CadLayoutGeometry.TryFreshBounds(mt) : CadLayoutGeometry.TryBounds(e);
            return b is null ? null : new Rect2(b.MinX,b.MinY,b.MaxX,b.MaxY); }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return null; }
    }
}

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

// Bilingual-only: preserve the complete original and copy a target-only grid nearby.
internal static class BilingualTableCopy
{
    internal sealed record CopyReceipt(string SourceHandle, string TargetHandle, double Dx, double Dy, string? TargetText, Rect2 Table);

    internal static HashSet<string> Apply(Database db, Transaction tx, CadLayoutBaseline baseline,
        LayoutWriteInput[] inputs, Dictionary<string, List<Rect2>> occupied, string language,
        List<BilingualDrawingImporter.Pair> pairs, List<CopyReceipt> receipts, List<object> decisions,
        Dictionary<string, BilingualGroupLayout.Slot> inlineSlots, HashSet<string> tableIds, HashSet<string> blocked,
         CadObjectAccess? access = null,
         IReadOnlyDictionary<string, IReadOnlyList<Rect2[]>>? tableGroups = null,
         HashSet<string>? released = null)
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
            var groups = tableGroups?.GetValueOrDefault(definition.Name) ??
                BilingualTableLayout.TranslationGroups(definition.Regions, definition.BoundarySegments,
                    definition.Texts.Where(t => byId.ContainsKey(t.RecordId)).Select(t =>
                        (t.Source.Bounds, BilingualDrawingImporter.Plain(byId[t.RecordId].Manifest.RawText))).ToArray());
            // A full translated grid must not occupy another table/signature
            // grid's empty cells. This does not change ordinary label clearance.
            var gridReservations=BilingualTableLayout.CompleteGroups(definition.Regions,definition.BoundarySegments)
                .SelectMany(cells=>cells).Distinct().ToArray();
            foreach (var cells in groups)
            {
                if (cells.Length < 3 || !BilingualTableLayout.HasRealColumns(cells) || cells.Select(c => Math.Round(c.Center.Y, 3)).Distinct().Count() < 2) continue;
                var table = new Rect2(cells.Min(c => c.Left), cells.Min(c => c.Bottom), cells.Max(c => c.Right), cells.Max(c => c.Top));
                var sources = definition.Texts.Where(t => byId.ContainsKey(t.RecordId) &&
                    cells.Any(c => c.Contains(new Rect2(t.Source.Bounds.Center.X,t.Source.Bounds.Center.Y,t.Source.Bounds.Center.X,t.Source.Bounds.Center.Y)))).ToArray();
                var requests = sources.Where(t => changed.ContainsKey(t.RecordId) && !handled.Contains(t.RecordId)).ToArray();
                // A real multi-row grid can have just one untranslated heading;
                // numeric/code-only cells still establish its table structure.
                if (requests.Length == 0 || sources.Length < 2)
                {
                    // Not a complete-copy table: release members so group planning
                    // keeps them on ordinary in-place label placement instead of
                    // blocking a group that will never be copied.
                    foreach (var t in sources) released?.Add(t.RecordId);
                    continue;
                }
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
                    !BilingualLabelEquivalence.MatchesInline(BilingualDrawingImporter.Plain(byId[t.RecordId].Manifest.RawText),
                        BilingualDrawingImporter.Plain(changed[t.RecordId].RestoredText))).ToArray();
                foreach(var t in sources) tableIds.Add(t.RecordId);
                if (pending.Length == 0)
                {
                    decisions.Add(new{table,strategy="existing-bilingual-table",reused=reuse.Count});
                    continue;
                }
                // A table whose cells already carry target text (title blocks and
                // schedules translated in the source drawing) is never cloned into a
                // second table. A cell that already has a target-language neighbour
                // is paired to it; only the cells that truly lack a counterpart get
                // an in-place label under their own text.
                var latinCells=sources.Where(o=>!System.Text.RegularExpressions.Regex.IsMatch(
                    BilingualDrawingImporter.Plain(byId[o.RecordId].Manifest.RawText),@"[\u3400-\u9fff\uf900-\ufaff]")).ToArray();
                if(latinCells.Length>0 && latinCells.Length*5>=sources.Length*2)
                {
                    // A cell lookup can miss (a label may sit between grid lines);
                    // treat a miss as "no counterpart" instead of throwing.
                    Rect2? CellOrNull(CadLayoutText t) => cells
                        .Where(c => c.Contains(new Rect2(t.Source.Bounds.Center.X,t.Source.Bounds.Center.Y,t.Source.Bounds.Center.X,t.Source.Bounds.Center.Y)))
                        .OrderBy(c => c.Area).Cast<Rect2?>().FirstOrDefault();
                    int fresh=0;
                    foreach(var t in pending)
                    {
                        var cell = CellOrNull(t);
                        var mate=cell is null ? Array.Empty<CadLayoutText>()
                            : latinCells.Where(o=>o.RecordId!=t.RecordId && CellOrNull(o)==cell).ToArray();
                        if(mate.Length>0)
                        {
                            pairs.Add(new(t.RecordId,t.EntityHandle,mate[0].EntityHandle,
                                BilingualDrawingImporter.Plain(byId[mate[0].RecordId].Manifest.RawText),
                                "existing-neighbor",definition.Name,mate[0].Source.Bounds,1));
                            handled.Add(t.RecordId);
                            continue;
                        }
                        tableIds.Remove(t.RecordId); blocked.Remove(t.RecordId); released?.Add(t.RecordId); fresh++;
                    }
                    decisions.Add(new{table,strategy="existing-bilingual-table",reason="cells-already-bilingual",
                        latinCells=latinCells.Length,requests=requests.Length,reused=reuse.Count,fresh,pending=pending.Length});
                    continue;
                }
                // A failed copy stays a grouped repair; it must not scatter into tiny labels.
                foreach(var t in pending) blocked.Add(t.RecordId);
                // A copy that cannot complete must not strand its members either:
                // release them so ordinary label placement can fill each cell in place.
                void Release()
                {
                    foreach (var t in requests)
                    { tableIds.Remove(t.RecordId); blocked.Remove(t.RecordId); released?.Add(t.RecordId); }
                }
                var owner = access.Read<BlockTableRecord>(db.GetObjectId(false, new Handle(Convert.ToInt64(definition.Handle, 16)), 0), "table-owner", definition.Name);
                if (owner is null) { decisions.Add(new { table, strategy="table-copy-unresolved", reason="unreadable-table-owner" }); Release(); continue; }
                var members = new List<Entity>();
                bool unsupported = false;
                string? unsupportedHandle = null;
                string? unsupportedType = null;
                string? unsupportedReason = null;
                foreach (ObjectId id in owner)
                {
                    var entity = access.Read<Entity>(id, "table-member", definition.Name, owner.Handle.ToString());
                    if (entity is null) { unsupported = true; unsupportedReason = "unreadable-member"; break; }
                    if (entity is Line line) { if (!Clip(line,table,out _,out _)) continue; }
                    else
                    {
                        if (Bounds(entity) is not { } b) continue;
                        if (!table.Contains(b, 1e-5))
                        {
                            // A crossing block cannot be partially discarded from
                            // a supposedly complete copy. Enclosing sheet frames
                            // are not members and remain outside this table unit.
                            if (entity is BlockReference crossing && !b.Contains(table, 1e-5) && Overlap(table, b, 0))
                            {
                                IsPureGridBlock(tx, crossing, table, access, out var crossingReason);
                                if (crossingReason is not null && (crossingReason.StartsWith("non-line:MText:") ||
                                    crossingReason.StartsWith("non-line:DBText:") ||
                                    crossingReason.StartsWith("non-line:LineAngularDimension2:"))) continue;
                                unsupported = true; unsupportedReason = "crossing-block:" + crossingReason;
                                unsupportedHandle = entity.Handle.ToString(); unsupportedType = entity.GetType().Name; break;
                            }
                            continue;
                        }
                    }
                    if (entity is BlockReference grid)
                    {
                        if (!IsPureGridBlock(tx, grid, table, access, out var gridReason))
                        {
                            // Text-bearing equipment blocks can sit inside the
                            // schedule's envelope. They are not its grid, and
                            // cloning them would duplicate unrelated artwork.
                            if (gridReason is not null && (gridReason.StartsWith("non-line:MText:") || gridReason.StartsWith("non-line:DBText:"))) continue;
                            unsupported = true; unsupportedReason = "mixed-grid-block:" + gridReason;
                            unsupportedHandle = entity.Handle.ToString(); unsupportedType = entity.GetType().Name; break;
                        }
                    }
                    else if (entity is Polyline polyline && !GridPolyline(polyline))
                    { unsupported = true; unsupportedReason = "unsupported-grid-polyline"; unsupportedHandle = entity.Handle.ToString(); unsupportedType = entity.GetType().Name; break; }
                    else if (entity is not Line and not Polyline and not MText and not DBText) { unsupported = true; unsupportedReason = "unsupported-member"; unsupportedHandle = entity.Handle.ToString(); unsupportedType = entity.GetType().Name; break; }
                    members.Add(entity);
                }
                var missing=requests.Where(t=>!members.Any(m=>m.ObjectId==t.ObjectId)).Select(t=>t.EntityHandle).ToArray();
                bool noGrid=!members.Any(m=>m is Line or Polyline or BlockReference);
                if (unsupported || noGrid || missing.Length>0)
                {
                    decisions.Add(new { table, strategy="table-copy-unresolved",
                        reason=unsupported ? "unsupported-member" : noGrid ? "no-grid-linework" : "request-member-not-clonable",
                        unsupportedReason,unsupportedHandle,unsupportedType,memberCount=members.Count,cells,missingRequestHandles=missing });
                    Release();
                    continue;
                }

                // A confirmed inline record can deliberately preserve both
                // languages. It is valid in the original, but is not a target-
                // only translation for an otherwise incomplete table copy.
                var untranslatedMembers=members.Where(m=>m is MText or DBText).Where(m=>
                {
                    if(sources.Any(t=>t.ObjectId==m.ObjectId && changed.ContainsKey(t.RecordId)))return false;
                    string value=m is MText mt ? mt.Text : ((DBText)m).TextString;
                    return !language.StartsWith("zh",StringComparison.OrdinalIgnoreCase) &&
                        System.Text.RegularExpressions.Regex.IsMatch(value,@"[\u3400-\u9fff\uf900-\ufaff]");
                }).Select(m=>m.Handle.ToString()).ToArray();
                if(untranslatedMembers.Length>0)
                {
                    decisions.Add(new{table,strategy="table-copy-unresolved",reason="target-only-table-text-required",
                        untranslatedMembers,memberCount=members.Count,cells});
                    Release();
                    continue;
                }

                double gap = cells.Select(c=>c.Height).Order().ElementAt(cells.Length/2)*.25;
                var detectedFrames = DetectFrames(definition.BoundarySegments, table);
                var frames = definition.Regions.Concat(detectedFrames.Select(b => new LayoutRegion("table-copy-frame",LayoutRegionKind.ClosedFrame,b))).Where(r => BilingualTablePlacement.IsContainingFrame(table,r.Bounds))
                    .OrderBy(r => r.Bounds.Area).ToArray();
                var allFrames=definition.Regions.Where(r=>r.Kind==LayoutRegionKind.ClosedFrame).Select(r=>r.Bounds)
                    .Concat(frames.Select(f=>f.Bounds)).Concat(baseline.Definitions.Where(d=>d.Name!=definition.Name)
                        .SelectMany(d=>d.Regions.Where(r=>r.Kind==LayoutRegionKind.ClosedFrame).SelectMany(r=>
                            InstanceOccupancyProjection.Project(r.Bounds,d.Name,definition.Name,baseline.BlockInstances)))).Distinct().ToArray();
                var offsets = BilingualTablePlacement.CompleteCopySearch(table,gap,occupied[definition.Name],allFrames)
                    .Select(b=>new Vector3d(b.Left-table.Left,b.Bottom-table.Bottom,0)).ToArray();
                Vector3d? selected = null;
                foreach (var offset in offsets)
                {
                    Rect2 destination = Move(table, offset);
                    if(!BilingualTablePlacement.AvoidsOtherTableFrames(table,destination,DetectFrames(definition.BoundarySegments,destination)))continue;
                    if (occupied[definition.Name].Any(b => Overlap(destination, b, gap*.1))) continue;
                    if (gridReservations.Any(b=>Overlap(destination,b,gap*.1))) continue;
                    if (CrossesDrawingGeometry(definition,table,destination,gap,frames.Select(f=>f.Bounds))) continue;
                    selected = offset; break;
                }
                if (selected is not { } delta)
                { decisions.Add(new { table, strategy = "table-copy-unresolved", reason = "no-immediately-adjacent-space", frames = frames.Select(f => f.Bounds).ToArray(), candidates = offsets.Select(o => new { destination = Move(table,o), text = occupied[definition.Name].Count(b => Overlap(Move(table,o),b,gap*.1)), lines = definition.BoundarySegments.Where(s => Overlap(Move(table,o),new Rect2(s.MinX,s.MinY,s.MaxX,s.MaxY),gap*.1)).ToArray(), geometry = definition.ProtectedGeometry.Count(g => !g.Bounds.Contains(Move(table,o)) && Overlap(Move(table,o),g.Bounds,gap*.1)) }).ToArray() }); Release(); continue; }

                var staged = new List<(Entity Source, Entity Clone, CadLayoutText? Text, string? Target)>();
                bool fits = true;
                string? failedHandle = null;
                object? failedDetails = null;
                foreach (var member in members)
                {
                    // In a target-only copy, one existing equivalent target replaces the paired source label.
                    if(sources.Any(t=>t.ObjectId==member.ObjectId && reuse.ContainsKey(t.RecordId))) continue;
                    // If a title cell already contains English that was not
                    // selected as the reviewed equivalent, use the requested
                    // translation in the copy without a second English label.
                    var existingMember=sources.FirstOrDefault(t=>t.ObjectId==member.ObjectId);
                    if(language.StartsWith("en",StringComparison.OrdinalIgnoreCase) && existingMember is not null &&
                        !changed.ContainsKey(existingMember.RecordId) && !reuse.Values.Any(t=>t.ObjectId==member.ObjectId) &&
                        System.Text.RegularExpressions.Regex.IsMatch(BilingualDrawingImporter.Plain(byId[existingMember.RecordId].Manifest.RawText),@"^[A-Za-z][A-Za-z .-]*$") &&
                        sources.Any(t=>changed.ContainsKey(t.RecordId) && Cell(t)==Cell(existingMember) &&
                            BilingualTitlePanel.IsTitleField(BilingualDrawingImporter.Plain(byId[t.RecordId].Manifest.RawText))))
                        continue;
                    var text = sources.FirstOrDefault(t => t.ObjectId == member.ObjectId && changed.ContainsKey(t.RecordId));
                    var alias=reuse.FirstOrDefault(p=>p.Value.ObjectId==member.ObjectId);
                    if(alias.Key is not null) text=sources.First(t=>t.RecordId==alias.Key);
                    Entity clone;
                    if(member is BlockReference originalGrid)
                    {
                        // Clone() copies persistent associative-array reactors:
                        // saving that shared dependency can suppress the original
                        // grid's display even while its geometry signature passes.
                        var grid=new BlockReference(Point3d.Origin,originalGrid.BlockTableRecord);
                        grid.SetPropertiesFrom(originalGrid);
                        grid.TransformBy(originalGrid.BlockTransform);
                        clone=grid;
                    }
                    else clone = (Entity)member.Clone();
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
                    decisions.Add(new { table, strategy = "table-copy-unresolved", reason = "copy-text-does-not-fit", failedHandle, failedDetails }); Release(); continue;
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
                var nearbyCandidates = offsets.Take(8).Select(o => new { destination = Move(table,o),
                    textOverlap = occupied[definition.Name].Count(b => Overlap(Move(table,o),b,gap*.1)),
                    sourceTextBlockers = definition.Texts.Where(t=>Overlap(Move(table,o),t.Source.Bounds,gap*.1))
                        .Select(t=>new { t.EntityHandle, t.ObjectType, t.Source.Bounds, text=t.CandidateText }).Take(12).ToArray(),
                    gridOverlap = gridReservations.Count(b => Overlap(Move(table,o),b,gap*.1)),
                    drawingGeometryBlocked = CrossesDrawingGeometry(definition,table,Move(table,o),gap,frames.Select(f=>f.Bounds)),
                    frameBlocked = !BilingualTablePlacement.AvoidsOtherTableFrames(table,Move(table,o),DetectFrames(definition.BoundarySegments,Move(table,o))) }).ToArray();
                var envelope = Move(table,delta);
                occupied[definition.Name].Add(envelope);
                foreach (var name in occupied.Keys.Where(n => n != definition.Name))
                    occupied[name].AddRange(InstanceOccupancyProjection.Project(envelope,definition.Name,name,baseline.BlockInstances));
                decisions.Add(new { table, destination = envelope, strategy = "table-copy", reason = "complete-target-language-table", gap,
                    outsideContainingFrame=frames.Length>0 && !frames.Any(f=>f.Bounds.Contains(envelope)),
                    nearbyCandidates,
                    copiedEntities = staged.Count, translatedCells = staged.Count(s => s.Target != null) });
            }
        }
        return handled;
    }

    private static bool CrossesDrawingGeometry(CadDefinitionTopology definition, Rect2 source,
        Rect2 destination, double gap, IEnumerable<Rect2> sourceFrames)
    {
        double inset=Math.Min(gap*.5,Math.Min(destination.Width,destination.Height)*.05);
        var interior=new Rect2(destination.Left+inset,destination.Bottom+inset,
            destination.Right-inset,destination.Top-inset);
        return definition.BoundarySegments.Any(s=>!IsSourceFrameEdge(s,sourceFrames) &&
                BilingualDrawingImporter.Crosses(interior,s)) ||
            definition.ProtectedGeometry.Any(g=>!g.Bounds.Contains(source) && Overlap(interior,g.Bounds,0));
    }

    private static bool IsSourceFrameEdge(Segment2 segment, IEnumerable<Rect2> frames)
    {
        const double tolerance=1e-3;
        return frames.Any(frame =>
            segment.IsHorizontal(tolerance) &&
                (Math.Abs(segment.Start.Y-frame.Bottom)<tolerance || Math.Abs(segment.Start.Y-frame.Top)<tolerance) &&
                segment.MinX<=frame.Left+tolerance && segment.MaxX>=frame.Right-tolerance ||
            segment.IsVertical(tolerance) &&
                (Math.Abs(segment.Start.X-frame.Left)<tolerance || Math.Abs(segment.Start.X-frame.Right)<tolerance) &&
                segment.MinY<=frame.Bottom+tolerance && segment.MaxY>=frame.Top-tolerance);
    }

    private static bool Fit(Entity entity, string target, Rect2 cell, string language)
    {
        double originalHeight = entity is MText mt ? mt.TextHeight : ((DBText)entity).Height;
        foreach (double scale in new[] {1.0,.9,.8,.7,.65,.6})
        {
            // Padding follows the measured target size, not the larger source
            // size; keep real clearance without rejecting a readable wrapped row.
            double inset=originalHeight*scale*.15;
            if(cell.Width<=2*inset || cell.Height<=2*inset)continue;
            var inner=new Rect2(cell.Left+inset,cell.Bottom+inset,cell.Right-inset,cell.Top-inset);
            if (entity is MText text)
            {
                // New target text uses the same language-capable font policy as
                // other bilingual labels, not a source-only legacy SHX face.
                text.Contents = BilingualGroupLayout.Contents(target,language);
                text.TextHeight = originalHeight*scale;
                text.Width = 0;
                if (text.ActualWidth*CadLayoutGeometry.MTextMeasurementSafetyScale > inner.Width)
                {
                    text.Width = inner.Width/CadLayoutGeometry.MTextMeasurementSafetyScale;
                    // Native word wrapping may slightly exceed the requested
                    // width; correct the measured width before trying smaller text.
                    for(int attempt=0;attempt<3 && text.ActualWidth*CadLayoutGeometry.MTextMeasurementSafetyScale>inner.Width;attempt++)
                        text.Width*=inner.Width/(text.ActualWidth*CadLayoutGeometry.MTextMeasurementSafetyScale)*.995;
                }
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
            if (copy.Table.Area <= 0 || !double.IsFinite(copy.Table.Area))
                throw new CommandProtocolException("bilingual_table_copy_mismatch", "A copied table requires valid persisted source bounds.");
            Entity Get(string h)
            {
                var id = db.GetObjectId(false, new Handle(Convert.ToInt64(h, 16)), 0);
                if (id.IsNull || !id.IsValid || id.IsErased || tx.GetObject(id, OpenMode.ForRead) is not Entity entity)
                    throw new CommandProtocolException("bilingual_table_copy_mismatch", $"Missing table entity {h}.");
                return entity;
            }
            var source = Get(copy.SourceHandle); var target = Get(copy.TargetHandle);
            bool sameClass=source.GetRXClass().Name == target.GetRXClass().Name, sameLayer=source.Layer == target.Layer;
            bool sameColor=source.Color == target.Color, sameLineType=source.LinetypeId == target.LinetypeId;
            bool sameLineWeight=source.LineWeight == target.LineWeight;
            bool valid = sameClass && sameLayer && sameColor && sameLineType && sameLineWeight;
            string geometry="";
            if (source is Line a && target is Line b)
            {
                var delta = new Vector3d(copy.Dx,copy.Dy,0);
                bool clipped = Clip(a, copy.Table, out var start, out var end);
                bool forward=(start+delta).DistanceTo(b.StartPoint)<1e-5 && (end+delta).DistanceTo(b.EndPoint)<1e-5;
                bool reversed=(start+delta).DistanceTo(b.EndPoint)<1e-5 && (end+delta).DistanceTo(b.StartPoint)<1e-5;
                valid &= clipped && (forward || reversed);
                geometry=$" clipped={clipped} forward={forward} reversed={reversed} expected={start+delta}|{end+delta} actual={b.StartPoint}|{b.EndPoint}";
            }
            else if (source is Polyline aPoly && target is Polyline bPoly)
            {
                var delta = new Vector3d(copy.Dx,copy.Dy,0);
                bool shape=GridPolyline(aPoly) && aPoly.NumberOfVertices==bPoly.NumberOfVertices &&
                    aPoly.Closed==bPoly.Closed &&
                    Enumerable.Range(0,aPoly.NumberOfVertices).All(i=>
                        (aPoly.GetPoint3dAt(i)+delta).DistanceTo(bPoly.GetPoint3dAt(i))<1e-5 &&
                        Math.Abs(aPoly.GetBulgeAt(i)-bPoly.GetBulgeAt(i))<1e-8);
                valid &= shape;
                if (!shape) geometry=$" sourceCount={aPoly.NumberOfVertices} targetCount={bPoly.NumberOfVertices}"+
                    $" sourceClosed={aPoly.Closed} targetClosed={bPoly.Closed} sourceGrid={GridPolyline(aPoly)}"+
                    $" offset={delta} firstExpected={aPoly.GetPoint3dAt(0)+delta} firstActual={bPoly.GetPoint3dAt(0)}";
            }
            else if (source is BlockReference sourceBlock && target is BlockReference targetBlock)
            {
                var expected = Matrix3d.Displacement(new Vector3d(copy.Dx, copy.Dy, 0)) * sourceBlock.BlockTransform;
                valid &= sourceBlock.BlockTableRecord == targetBlock.BlockTableRecord &&
                    IsPureGridBlock(tx, sourceBlock, copy.Table, new CadObjectAccess(db, tx), out _) &&
                    expected.ToArray().Zip(targetBlock.BlockTransform.ToArray()).All(p => Math.Abs(p.First - p.Second) < 1e-5);
            }
            else if (source is MText or DBText && target is MText or DBText)
            {
                string Read(Entity e) => e is MText m ? m.Text : ((DBText)e).TextString;
                string expected = copy.TargetText ?? Read(source);
                static string Canonical(string value) => value.Replace("\r\n", "\n").Replace("\r", "\n");
                valid &= Canonical(Read(target)) == Canonical(expected);
                if (copy.TargetText is null && Bounds(source) is { } s && Bounds(target) is { } t)
                {
                    // AutoCAD can rejustify DBText after a clone is saved. Its
                    // visible box may move by a fraction of the glyph height.
                    // Keep the complete copied grid and measured glyph shape.
                    double shift=source is DBText ? s.Height*.65 : 1e-3;
                    var destination=Move(copy.Table,new Vector3d(copy.Dx,copy.Dy,0));
                    valid &= Math.Abs(t.Left-s.Left-copy.Dx)<=shift &&
                        Math.Abs(t.Bottom-s.Bottom-copy.Dy)<=shift &&
                        Math.Abs(t.Width-s.Width)<1e-3 && Math.Abs(t.Height-s.Height)<1e-3 &&
                        destination.Contains(new Rect2(t.Center.X,t.Center.Y,t.Center.X,t.Center.Y),1e-5);
                }
                if (!valid) geometry=$" expectedText={expected} actualText={Read(target)} sourceText={Read(source)} targetOverride={copy.TargetText is not null}"+
                    $" sourceBounds={Bounds(source)} targetBounds={Bounds(target)} offset={copy.Dx},{copy.Dy}";
            }
            else valid = false;
            if (!valid) throw new CommandProtocolException("bilingual_table_copy_mismatch",$"Invalid table copy {copy.SourceHandle} -> {copy.TargetHandle}; type={source.GetType().Name}/{target.GetType().Name}, class={sameClass}, layer={sameLayer}, color={sameColor}, linetype={sameLineType}, lineweight={sameLineWeight}.{geometry}");
        }
    }

    // The parent may contain text while repeated row separators live several
    // definitions below it. Preserve a pure grid's nested structure as a block;
    // never duplicate a mixed equipment/text block just because its bbox fits.
    private static bool IsPureGridBlock(Transaction tx, BlockReference root, Rect2 table, CadObjectAccess access, out string? reason)
    {
        var active = new HashSet<ObjectId>();
        int visited = 0, lineCount = 0;
        string? failure = null;
        bool Walk(BlockReference reference, Matrix3d transform, int depth)
        {
            if (depth > 32 || ++visited > 4096 || reference.AttributeCollection.Count != 0 ||
                !active.Add(reference.BlockTableRecord)) { failure = "depth-count-attributes-or-cycle:" + reference.Handle; return false; }
            try
            {
                var definition = access.Read<BlockTableRecord>(reference.BlockTableRecord, "table-grid-definition", null, reference.Handle.ToString());
                if (definition is null || definition.IsFromExternalReference || definition.IsFromOverlayReference) { failure = "xref-or-unreadable:" + reference.Handle; return false; }
                foreach (ObjectId id in definition)
                {
                    var member = access.Read<Entity>(id, "table-grid-member", definition.Name);
                    if (member is BlockReference nested)
                    {
                        if (!Walk(nested, transform * nested.BlockTransform, depth + 1)) return false;
                    }
                    else if (member is Line line)
                    {
                        var a = line.StartPoint.TransformBy(transform);
                        var b = line.EndPoint.TransformBy(transform);
                        if (Math.Abs(a.Z - b.Z) > 1e-5 || a.DistanceTo(b) <= 1e-5 ||
                            Math.Abs(a.X - b.X) > 1e-5 && Math.Abs(a.Y - b.Y) > 1e-5 ||
                            !table.Contains(new Rect2(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)), 1e-5))
                        { failure = "non-axis-or-outside-line:" + line.Handle; return false; }
                        lineCount++;
                    }
                    else { failure = "non-line:" + member?.GetType().Name + ":" + member?.Handle; return false; }
                }
                return true;
            }
            finally { active.Remove(reference.BlockTableRecord); }
        }
        bool pure = Walk(root, root.BlockTransform, 0) && lineCount > 0;
        reason = pure ? null : failure ?? "no-lines";
        return pure;
    }

    private static bool GridPolyline(Polyline line)
    {
        if (line.NumberOfVertices<2 || !line.Normal.IsEqualTo(Vector3d.ZAxis)) return false;
        int segments=line.Closed ? line.NumberOfVertices : line.NumberOfVertices-1;
        for (int i=0;i<segments;i++)
        {
            if (Math.Abs(line.GetBulgeAt(i))>1e-8) return false;
            var a=line.GetPoint2dAt(i); var b=line.GetPoint2dAt((i+1)%line.NumberOfVertices);
            if (a.GetDistanceTo(b)<1e-5 ||
                Math.Abs(a.X-b.X)>1e-5 && Math.Abs(a.Y-b.Y)>1e-5) return false;
        }
        return true;
    }

    private static bool Clip(Line line,Rect2 table,out Point3d start,out Point3d end)
    {
        const double tolerance=1e-5;
        start=line.StartPoint; end=line.EndPoint;
        if (Math.Abs(start.Y-end.Y)<tolerance && start.Y>=table.Bottom-tolerance && start.Y<=table.Top+tolerance)
        {
            double left=Math.Max(table.Left,Math.Min(start.X,end.X)),right=Math.Min(table.Right,Math.Max(start.X,end.X));
            if(right-left<=tolerance) return false;
            bool forward=start.X<end.X; start=new Point3d(forward?left:right,start.Y,start.Z); end=new Point3d(forward?right:left,end.Y,end.Z); return true;
        }
        if (Math.Abs(start.X-end.X)<tolerance && start.X>=table.Left-tolerance && start.X<=table.Right+tolerance)
        {
            double bottom=Math.Max(table.Bottom,Math.Min(start.Y,end.Y)),top=Math.Min(table.Top,Math.Max(start.Y,end.Y));
            if(top-bottom<=tolerance) return false;
            bool forward=start.Y<end.Y; start=new Point3d(start.X,forward?bottom:top,start.Z); end=new Point3d(end.X,forward?top:bottom,end.Z); return true;
        }
        return false;
    }

    internal static IEnumerable<Rect2> DetectFrames(IReadOnlyList<Segment2> segments, Rect2 table)
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

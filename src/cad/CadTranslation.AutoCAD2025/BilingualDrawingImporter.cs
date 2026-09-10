using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadTranslation.Contracts;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

/// <summary>Additive bilingual editing. No source entity is opened for write.</summary>
internal static class BilingualDrawingImporter
{
    private const string SourceLinkApp = "AGICO_CAD_BILINGUAL";

    private static bool HasSourceLink(Entity entity, ManifestRecord source)
    {
        using var data = entity.GetXDataForApplication(SourceLinkApp);
        var values = data?.AsArray();
        return values is { Length: 3 } &&
            string.Equals(values[1].Value as string, source.Handle, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(values[2].Value as string, Hashing.Sha256Text(source.RawText), StringComparison.Ordinal);
    }

    private static void LinkSource(Database db, Transaction tx, Entity target, ManifestRecord source)
    {
        var apps = (RegAppTable)tx.GetObject(db.RegAppTableId, OpenMode.ForRead);
        if (!apps.Has(SourceLinkApp))
        {
            apps.UpgradeOpen();
            var app = new RegAppTableRecord { Name = SourceLinkApp };
            apps.Add(app);
            tx.AddNewlyCreatedDBObject(app, true);
        }
        using var data = new ResultBuffer(new TypedValue(1001, SourceLinkApp),
            new TypedValue(1000, source.Handle), new TypedValue(1000, Hashing.Sha256Text(source.RawText)));
        target.XData = data;
    }
    internal sealed record Pair(string RecordId, string SourceHandle, string TargetHandle,
        string TargetText, string Decision, string DefinitionName, Rect2 Bounds, double HeightScale, string PlacementStrategy = "nearby");

    internal static int Run(JobContext context)
    {
        context.VerifySourceAndWorkingHashes();
        ManifestRecord[] manifest = NativeDrawing.ReadRows<ManifestRecord>(context.Config.ManifestPath);
        TranslationRecord[] translations = NativeDrawing.ReadRows<TranslationRecord>(context.Config.TranslationPath!);
        var validation = TranslationValidator.ValidateBatch(manifest, translations);
        if (!validation.IsValid) throw new CommandProtocolException("bilingual_invalid_batch", string.Join(",", validation.Errors.Select(e => e.Code)));
        var translated = translations.ToDictionary(r => r.RecordId);
        var pairs = new List<Pair>();
        var unresolved = new List<string>();
        var unresolvedDetails = new List<object>();
        var tableCopies = new List<BilingualTableCopy.CopyReceipt>();
        var tableDecisions = new List<object>();
        string output = context.Config.OutputPath;
        if (Path.GetFullPath(output).Equals(Path.GetFullPath(context.Config.SourcePath), StringComparison.OrdinalIgnoreCase) ||
            Path.GetFullPath(output).Equals(Path.GetFullPath(context.Config.WorkingPath), StringComparison.OrdinalIgnoreCase))
            throw new CommandProtocolException("unsafe_output", "Bilingual output must be a separate file.");

        using (var db = NativeDrawing.Open(context.Config.WorkingPath))
        {
            var previous = HostApplicationServices.WorkingDatabase;
            try
            {
                HostApplicationServices.WorkingDatabase = db;
                using var tx = db.TransactionManager.StartTransaction();
                var inputList = new List<LayoutWriteInput>();
                foreach (var row in manifest)
                {
                    string restored = TranslationValidator.RestoreProtectedTokensForOutput(translated[row.RecordId].TranslatedText, row.ProtectedTokens);
                    try { inputList.Add(new LayoutWriteInput(Resolve(db, row.Handle), row, restored, false)); }
                    catch (CommandProtocolException) when (restored == row.RawText)
                    {
                        // Missing XREF diagnostic rows are exported for audit but have no ObjectId in the host database.
                        // They are safe to omit only when the translation is an exact passthrough.
                    }
                }
                var inputs = inputList.ToArray();
                var baseline = DrawingTopologyCapture.Capture(db, tx, inputs);
                // Serialized GeometricExtents can contain stale MText column bounds.
                // Use current font metrics before allocating whitespace beside originals.
                baseline = baseline with { Definitions = baseline.Definitions.Select(d => d with {
                    Texts = d.Texts.Select(t => {
                        if (tx.GetObject(t.ObjectId, OpenMode.ForRead) is MText mt && CadLayoutGeometry.TryFreshBounds(mt) is { } b)
                            return t with { Source = t.Source with { Bounds = new Rect2(b.MinX, b.MinY, b.MaxX, b.MaxY) } };
                        return t;
                    }).ToArray() }).ToArray() };
                var topology = baseline.Definitions.SelectMany(d => d.Texts).ToDictionary(t => t.RecordId);
                var definitions = baseline.Definitions.ToDictionary(d => d.Name);
                var tableGroups = baseline.Definitions.ToDictionary(d => d.Name, d => BilingualTableLayout.Groups(d.Regions));
                var occupied = baseline.Definitions.ToDictionary(d => d.Name, d => d.Texts.Select(t => t.Source.Bounds).ToList());
                foreach (var item in baseline.Definitions.SelectMany(d => d.Texts.Select(t => (Definition: d.Name, Bounds: t.Source.Bounds))).ToArray())
                    ReserveProjected(occupied, item.Definition, item.Bounds, baseline.BlockInstances, includeLocal: false);
                var rowsByHandle = manifest.ToDictionary(r => r.Handle, StringComparer.OrdinalIgnoreCase);
                var fixedLabels = BilingualFixedLabelPolicy.Select(inputs
                    .Where(input => topology.ContainsKey(input.Manifest.RecordId))
                    .Select(input => {
                        var text = topology[input.Manifest.RecordId];
                        return new BilingualFixedLabelSample(input.Manifest.RecordId, input.Manifest.RawText,
                            Plain(input.RestoredText), input.Manifest.ObjectType, text.DefinitionName,
                            text.Source.Bounds, text.Source.OriginalTextHeight);
                    }).ToArray());
                var copied = BilingualTableCopy.Apply(db, tx, baseline, inputs, occupied, context.Config.TargetLanguage, pairs, tableCopies, tableDecisions);
                var tableSlots = PlanTableSlots(db, tx, baseline, inputs.Where(i => !copied.Contains(i.Manifest.RecordId)).ToArray(), context.Config.TargetLanguage);
                foreach (var planned in tableSlots)
                    ReserveProjected(occupied, topology[planned.Key].DefinitionName, planned.Value.Slot, baseline.BlockInstances);

                foreach (var input in inputs)
                {
                    var row = input.Manifest;
                    if (copied.Contains(row.RecordId)) continue;
                    if (translated[row.RecordId].TranslatedText == row.PlainText) continue;
                    if (!topology.TryGetValue(row.RecordId, out var source)) { unresolved.Add(row.RecordId); continue; }
                    var original = (Entity)tx.GetObject(input.ObjectId, OpenMode.ForRead);
                    if (original is not DBText && original is not MText && original is not Dimension) { unresolved.Add(row.RecordId); continue; }
                    var definition = definitions[source.DefinitionName];
                    string targetText = Plain(input.RestoredText);
                    string normalized = Normalize(targetText);
                    string sourcePlain = Plain(row.RawText);
                    if (normalized.Length == 0) { unresolved.Add(row.RecordId); continue; }
                    if (fixedLabels.ExistingEnglishIdBySourceId.TryGetValue(row.RecordId, out string? existingRecordId) &&
                        topology.TryGetValue(existingRecordId, out var fixedEnglish))
                    {
                        string existingText = Plain(rowsByHandle[fixedEnglish.EntityHandle].RawText);
                        pairs.Add(new(row.RecordId, row.Handle, fixedEnglish.EntityHandle, existingText, "existing-neighbor", source.DefinitionName, fixedEnglish.Source.Bounds, 1));
                        continue;
                    }
                    if (context.Config.TargetLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase) &&
                        BilingualFixedLabelPolicy.ContainsEmbeddedEnglish(row.RawText))
                    {
                        pairs.Add(new(row.RecordId, row.Handle, row.Handle, sourcePlain, "existing-inline", source.DefinitionName, source.Source.Bounds, 1));
                        continue;
                    }
                    if (Normalize(sourcePlain).Contains(normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        pairs.Add(new(row.RecordId, row.Handle, row.Handle, targetText, "existing-inline", source.DefinitionName, source.Source.Bounds, 1));
                        continue;
                    }
                    var existing = definition.Texts.Where(t => t.RecordId != row.RecordId && rowsByHandle.ContainsKey(t.EntityHandle))
                        .Where(t => BilingualLabelEquivalence.Matches(targetText, Plain(rowsByHandle[t.EntityHandle].RawText)))
                        .Where(t => HasSourceLink((Entity)tx.GetObject(t.ObjectId, OpenMode.ForRead), row) ||
                            Distance(source.Source.Bounds, t.Source.Bounds) <= source.Source.OriginalTextHeight * 4 ||
                            IsTableRowNeighbor(source.Source.Bounds, t.Source.Bounds, tableGroups[source.DefinitionName]))
                        .Where(t => !pairs.Any(p => p.TargetHandle == t.EntityHandle))
                        .OrderBy(t => Distance(source.Source.Bounds, t.Source.Bounds)).FirstOrDefault();
                    if (existing is not null)
                    {
                        pairs.Add(new(row.RecordId, row.Handle, existing.EntityHandle, Plain(rowsByHandle[existing.EntityHandle].RawText), "existing-neighbor", source.DefinitionName, existing.Source.Bounds, 1));
                        continue;
                    }

                    var owner = OwningBlock(tx, original);
                    if (owner is null || owner.IsFromExternalReference) { unresolved.Add(row.RecordId); continue; }
                    using var added = new MText();
                    added.SetDatabaseDefaults(db);
                    added.LayerId = original.LayerId;
                    added.Color = original.Color;
                    added.TextStyleId = original switch { MText mt => mt.TextStyleId, DBText dt => dt.TextStyleId,
                        Dimension dm => dm.GetDimstyleData().Dimtxsty, _ => db.Textstyle };
                    added.Attachment = AttachmentPoint.TopLeft;
                    added.Normal = original switch { MText plane => plane.Normal, DBText dbText => dbText.Normal,
                        Dimension dimension => dimension.Normal, _ => Vector3d.ZAxis };
                    added.Rotation = row.Geometry.RotationRadians;
                    string contents = Escape(targetText);
                    if (context.Config.TargetLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) contents = @"\FSimSun;" + contents;
                    bool inTable = tableSlots.TryGetValue(row.RecordId, out var slot);
                    Rect2 bounds = default;
                    double scale = 0;
                    bool placed = inTable && PlaceTableSlot(added, contents, slot.Slot, slot.Height, row.Geometry.InsertionPoint.Z, out bounds);
                    if (placed) scale = slot.Height / source.Source.OriginalTextHeight;
                    if (!placed && !Place(added, contents, source, definition, occupied[source.DefinitionName], row.Geometry.InsertionPoint.Z, out bounds, out scale))
                    { unresolved.Add(row.RecordId); unresolvedDetails.Add(new { row.RecordId, row.Handle, source.Source,
                        source.Region, actualWidth = added.ActualWidth, actualHeight = added.ActualHeight,
                        nearbyText = definition.Texts.Where(t => Distance(source.Source.Bounds, t.Source.Bounds) < source.Source.OriginalTextHeight * 4)
                            .Select(t => new { t.EntityHandle, text = rowsByHandle.GetValueOrDefault(t.EntityHandle)?.RawText, t.Source.Bounds }),
                        occupied = occupied[source.DefinitionName].Where(b => Distance(source.Source.Bounds, b) < source.Source.OriginalTextHeight * 4).Distinct().Take(60),
                        instances = baseline.BlockInstances.Where(i => i.DefinitionId == source.DefinitionName)
                            .Select(i => new { i.Path, bounds = i.WorldTransform.Apply(source.Source.Bounds) }) }); continue; }
                    owner.UpgradeOpen();
                    owner.AppendEntity(added);
                    tx.AddNewlyCreatedDBObject(added, true);
                    LinkSource(db, tx, added, row);
                    ReserveProjected(occupied, source.DefinitionName, bounds, baseline.BlockInstances);
                    pairs.Add(new(row.RecordId, row.Handle, added.Handle.ToString(), targetText, "added", source.DefinitionName, bounds, scale, inTable ? "table-side-column" : "cell-local-or-nearby"));
                }
                NativeDrawing.Report(context, "bilingual-review-windows.json", new {
                    windows = pairs.SelectMany(p => baseline.BlockInstances.Where(i => i.DefinitionId == p.DefinitionName)
                        .Select(i => {
                            Rect2 a = topology[p.RecordId].Source.Bounds, b = p.Bounds;
                            double margin = topology[p.RecordId].Source.OriginalTextHeight * 3;
                            Rect2 local = new(Math.Min(a.Left, b.Left) - margin, Math.Min(a.Bottom, b.Bottom) - margin,
                                Math.Max(a.Right, b.Right) + margin, Math.Max(a.Top, b.Top) + margin);
                            return new { p.SourceHandle, p.TargetHandle, p.Decision, p.HeightScale,
                                sourceText = rowsByHandle[p.SourceHandle].RawText, p.TargetText,
                                instancePath = i.Path, bounds = i.WorldTransform.Apply(local) };
                        })) });
                tx.Commit();
                NativeDrawing.Save(db, output);
            }
            finally { HostApplicationServices.WorkingDatabase = previous; }
        }

        NativeDrawing.Report(context, "bilingual-pairs.json", new { outputMode = "bilingual", pairs, unresolved });
        NativeDrawing.Report(context, "bilingual-unresolved.json", unresolvedDetails);
        // Reopen saved DWG before proving source retention and target associations.
        NativeDrawing.ExportCandidate(context);
        var candidate = NativeDrawing.ReadRows<ManifestRecord>(Path.Combine(context.Config.ArtifactDirectory, "bilingual-candidate.jsonl"));
        var candidateByHandle = candidate.ToDictionary(r => r.Handle, StringComparer.OrdinalIgnoreCase);
        var changedSources = manifest.Where(r => !candidateByHandle.TryGetValue(r.Handle, out var current)
                ? translated[r.RecordId].TranslatedText != r.FormatTemplate
                : !SourcePreserved(r, current)).Select(r => r.RecordId).ToArray();
        var missingTargets = pairs.Where(p => !candidateByHandle.TryGetValue(p.TargetHandle, out var target) ||
            !Normalize(Plain(target.RawText)).Contains(Normalize(p.TargetText), StringComparison.OrdinalIgnoreCase)).Select(p => p.RecordId).ToArray();
        BilingualTableCopy.Verify(context, tableCopies);
        DrawingVerifier.VerifyStructure(context, "bilingual-structure.json", tableCopies.Select(c => c.TargetHandle).ToHashSet(StringComparer.OrdinalIgnoreCase));
        NativeDrawing.Report(context, "bilingual-table-layout.json", new { tables = tableDecisions, copies = tableCopies });
        bool passed = changedSources.Length == 0 && missingTargets.Length == 0 && unresolved.Count == 0;
        NativeDrawing.Report(context, "bilingual-native-check.json", new { status = passed ? "passed" : "failed", outputMode = "bilingual",
            sourceRetainedCount = manifest.Length - changedSources.Length, changedSources, missingTargets, unresolved,
            addedCount = pairs.Count(p => p.Decision == "added"), skippedExistingCount = pairs.Count(p => p.Decision != "added"),
            candidateSha256 = Hashing.Sha256File(output), sourceSha256 = context.Config.SourceSha256 });
        NativeDrawing.Report(context, "bilingual-layout-audit.json", new { candidateReopened = true,
            texts = pairs, risks = Array.Empty<object>(), manualReview = unresolved,
            missingBlockInstancePaths = Array.Empty<string>(), riskCounts = new { low = 0, medium = 0, high = unresolved.Count } });
        NativeDrawing.Report(context, "bilingual-composition.json", new { status = "not-applicable", reason = "source-preserving additive placement" });
        if (!passed) throw new CommandProtocolException("bilingual_verification_failed", $"Changed source: {changedSources.Length}; missing target: {missingTargets.Length}; no safe placement: {unresolved.Count}.");
        return manifest.Length;
    }

    private static bool IsTableRowNeighbor(Rect2 source, Rect2 target, IReadOnlyList<Rect2[]> groups)
    {
        foreach (var cells in groups)
        {
            var row = cells.Where(c => c.Contains(source)).OrderBy(c => c.Area).ToArray();
            if (row.Length == 0) continue;
            double left = cells.Min(c => c.Left), right = cells.Max(c => c.Right);
            if (target.Bottom < row[0].Bottom || target.Top > row[0].Top) continue;
            double gap = target.Right <= left ? left - target.Right : target.Left >= right ? target.Left - right : double.PositiveInfinity;
            if (gap <= row[0].Height) return true;
        }
        return false;
    }

    private static Dictionary<string, (Rect2 Slot, double Height)> PlanTableSlots(Database db, Transaction tx,
        CadLayoutBaseline baseline, LayoutWriteInput[] inputs, string language)
    {
        var result = new Dictionary<string, (Rect2, double)>();
        var changed = inputs.Where(i => i.RestoredText != i.Manifest.RawText && i.RestoredText != i.Manifest.PlainText)
            .ToDictionary(i => i.Manifest.RecordId);
        foreach (var definition in baseline.Definitions)
        {
            var reserved = new List<Rect2>();
            foreach (var cells in BilingualTableLayout.Groups(definition.Regions))
            {
                var sources = definition.Texts.Where(t => changed.ContainsKey(t.RecordId) && cells.Any(c => c.Contains(t.Source.Bounds)))
                    .OrderByDescending(t => t.Source.Bounds.Center.Y).ToArray();
                if (sources.Length < 2) continue;
                var rows = sources.Select(t => cells.Where(c => c.Contains(t.Source.Bounds)).OrderBy(c => c.Area).First()).ToArray();
                // Multiple translated columns in a row need cell-local placement to keep association unambiguous.
                if (rows.Where((r, i) => rows.Take(i).Any(p => Math.Min(p.Top, r.Top) > Math.Max(p.Bottom, r.Bottom) + 1e-5)).Any()) continue;
                if (sources.Any(t => Math.Abs(changed[t.RecordId].Manifest.Geometry.RotationRadians) > 1e-6)) continue;
                double height = sources.Min(t => t.Source.OriginalTextHeight) * .65;
                double gap = height * .3;
                if (rows.Any(r => r.Height <= gap * 2 + height)) continue;
                double width = 0;
                foreach (var source in sources)
                {
                    var entity = tx.GetObject(source.ObjectId, OpenMode.ForRead);
                    if (entity is not DBText && entity is not MText) { width = 0; break; }
                    using var measure = new MText();
                    measure.SetDatabaseDefaults(db);
                    measure.TextStyleId = entity switch { MText mt => mt.TextStyleId, DBText dt => dt.TextStyleId,
                        Dimension dm => dm.GetDimstyleData().Dimtxsty, _ => db.Textstyle };
                    measure.TextHeight = height;
                    measure.Width = 0;
                    measure.Contents = (language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? @"\FSimSun;" : "") + Escape(Plain(changed[source.RecordId].RestoredText));
                    width = Math.Max(width, measure.ActualWidth * 1.08);
                }
                if (width <= 0) continue;
                var table = new Rect2(cells.Min(c => c.Left), cells.Min(c => c.Bottom), cells.Max(c => c.Right), cells.Max(c => c.Top));
                foreach (bool left in new[] { true, false })
                {
                    var slots = BilingualTableLayout.SideSlots(table, rows, width, gap, left);
                    var envelope = new Rect2(slots.Min(s => s.Left), table.Bottom, slots.Max(s => s.Right), table.Top);
                    var frame = definition.Regions.Where(r => r.Kind == LayoutRegionKind.ClosedFrame && r.Bounds.Contains(table) && r.Bounds.Area > table.Area * 1.05)
                        .OrderBy(r => r.Bounds.Area).FirstOrDefault();
                    if (frame is not null && !frame.Bounds.Contains(envelope)) continue;
                    if (definition.Texts.Any(t => Intersects(envelope, t.Source.Bounds, gap)) || reserved.Any(r => Intersects(envelope, r, gap))) continue;
                    if (definition.BoundarySegments.Any(s => Crosses(envelope, s))) continue;
                    if (definition.ProtectedGeometry.Any(g => !g.Bounds.Contains(table) && Intersects(envelope, g.Bounds, 0))) continue;
                    bool fits = true;
                    for (int i = 0; i < sources.Length; i++)
                    {
                        using var probe = new MText();
                        probe.SetDatabaseDefaults(db);
                        var entity = tx.GetObject(sources[i].ObjectId, OpenMode.ForRead);
                        probe.TextStyleId = entity switch { MText mt => mt.TextStyleId, DBText dt => dt.TextStyleId,
                            Dimension dm => dm.GetDimstyleData().Dimtxsty, _ => db.Textstyle };
                        string contents = (language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? @"\FSimSun;" : "") + Escape(Plain(changed[sources[i].RecordId].RestoredText));
                        if (!PlaceTableSlot(probe, contents, slots[i], height, 0, out _)) { fits = false; break; }
                    }
                    if (!fits) continue;
                    for (int i = 0; i < sources.Length; i++) result[sources[i].RecordId] = (slots[i], height);
                    reserved.Add(envelope);
                    break;
                }
            }
        }
        return result;
    }

    private static bool PlaceTableSlot(MText text, string contents, Rect2 slot, double height, double z, out Rect2 bounds)
    {
        text.TextHeight = height;
        text.Width = slot.Width;
        text.Contents = contents;
        double w = text.ActualWidth * 1.02, h = text.ActualHeight * 1.02;
        bounds = new Rect2(slot.Left, slot.Center.Y - h / 2, slot.Left + w, slot.Center.Y + h / 2);
        if (w <= 0 || h <= 0 || !slot.Contains(bounds)) return false;
        text.Location = new Point3d(bounds.Left, bounds.Top, z);
        return true;
    }

    private static void ReserveProjected(Dictionary<string, List<Rect2>> occupied, string definition, Rect2 bounds,
        IReadOnlyList<BlockInstancePath> instances, bool includeLocal = true)
    {
        if (includeLocal) occupied[definition].Add(bounds);
        foreach (string target in occupied.Keys.Where(name => name != definition))
            occupied[target].AddRange(InstanceOccupancyProjection.Project(bounds, definition, target, instances));
    }

    private static bool Place(MText text, string contents, CadLayoutText source, CadDefinitionTopology definition,
        List<Rect2> occupied, double z, out Rect2 result, out double usedScale)
    {
        Rect2 box = source.Source.Bounds;
        double height = source.Source.OriginalTextHeight;
        var container = definition.Regions.Where(r => r.Kind is LayoutRegionKind.TableCell or LayoutRegionKind.ClosedFrame)
            .Where(r => r.Bounds.Contains(box, height * .05)).OrderBy(r => r.Bounds.Area).FirstOrDefault();
        Rect2 allowed = container?.Bounds ?? new Rect2(box.Left - height * 16, box.Bottom - height * 16, box.Right + height * 16, box.Top + height * 16);
        // Every candidate must be inside allowed. Distant text cannot collide;
        // filter once instead of scanning every block's text for every trial.
        occupied = occupied.Where(o => Intersects(allowed, o, height * .12)).Distinct().ToList();
        foreach (double scale in BilingualPlacementPolicy.HeightScales)
        foreach (double widthFactor in new[] { 1.0, .8, .65, .5, .4 })
        {
            text.TextHeight = height * scale;
            text.Width = 0;
            text.Contents = "{\\W" + widthFactor.ToString(CultureInfo.InvariantCulture) + ";" + contents + "}";
            double unwrappedWidth = Math.Max(text.TextHeight, text.ActualWidth) * 1.05;
            foreach (double width in BilingualPlacementPolicy.CandidateWidths(allowed.Width, box.Width, height, unwrappedWidth))
            {
            text.Width = Math.Max(height, width);
            double w = Math.Max(text.TextHeight, text.ActualWidth) * 1.02, h = Math.Max(text.TextHeight, text.ActualHeight) * 1.02;
            if (!(w > 0 && h > 0)) continue;
            double gap = height * .12;
            for (int step = 0; step < (container is null ? 17 : 9); step++)
            {
                double offset = gap + height * step * .65;
                foreach (var point in new[] {
                    new Point3d(box.Left, box.Bottom - offset, 0),
                    new Point3d(box.Center.X - w / 2, box.Bottom - offset, 0),
                    new Point3d(box.Right - w, box.Bottom - offset, 0),
                    new Point3d(box.Left, box.Top + offset + h, 0),
                    new Point3d(box.Center.X - w / 2, box.Top + offset + h, 0),
                    new Point3d(box.Right - w, box.Top + offset + h, 0),
                    new Point3d(box.Right + offset, box.Top, 0),
                    new Point3d(box.Right + offset, box.Center.Y + h / 2, 0),
                    new Point3d(box.Left - offset - w, box.Top, 0) })
                {
                    text.Location = new Point3d(point.X, point.Y, z);
                    Rect2 bounds = TextBoundsEstimator.FromActualBox(new Point2(point.X, point.Y), w, h, TextAttachmentKind.TopLeft, text.Rotation);
                    if (!allowed.Contains(bounds, 1e-6) || occupied.Any(o => Intersects(bounds, o, height * .12))) continue;
                    if (definition.BoundarySegments.Any(line => Crosses(bounds, line))) continue;
                    if (definition.ProtectedGeometry.Any(g => !g.Bounds.Contains(box) && Intersects(bounds, g.Bounds, 0))) continue;
                    result = bounds; usedScale = scale; return true;
                }
            }
            }
        }
        // Last-resort bilingual placement: preserve text clearance, but do not let
        // thin process/frame geometry make a small translation impossible to add.
        {
            double scale = LayoutFitPolicy.EmergencyMinimumHeightScale;
            text.TextHeight = height * scale;
            text.Width = Math.Max(height, Math.Min(allowed.Width, height * 4));
            text.Contents = "{\\W0.4;" + contents + "}";
            double w = Math.Max(text.TextHeight, text.ActualWidth) * 1.02;
            double h = Math.Max(text.TextHeight, text.ActualHeight) * 1.02;
            double margin = Math.Min(height * .035, Math.Min(allowed.Width, allowed.Height) * .04);
            var otherText = occupied.ToArray();
            foreach (Rect2 fallback in BilingualPlacementPolicy.EmergencyCandidates(allowed, box, w, h, margin))
            {
                if (otherText.Any(o => Intersects(fallback, o, margin))) continue;
                text.Location = new Point3d(fallback.Left, fallback.Top, z);
                result = fallback;
                usedScale = scale;
                return true;
            }
            if (container is not null)
            {
                Rect2 cellBottom = BilingualPlacementPolicy.PlaceAtCellBottom(allowed, w, h, margin);
                if (allowed.Contains(cellBottom, 1e-6) && !otherText.Any(o => Intersects(cellBottom, o, margin)))
                {
                    text.Location = new Point3d(cellBottom.Left, cellBottom.Top, z);
                    result = cellBottom;
                    usedScale = scale;
                    return true;
                }
            }
        }
        result = box; usedScale = 0; return false;
    }

    internal static bool Intersects(Rect2 a, Rect2 b, double padding) =>
        a.Right > b.Left - padding && a.Left < b.Right + padding && a.Top > b.Bottom - padding && a.Bottom < b.Top + padding;

    private static bool Crosses(Rect2 b, Segment2 s)
    {
        // Segment/rectangle clipping, including diagonal process lines.
        double low = 0, high = 1, dx = s.End.X - s.Start.X, dy = s.End.Y - s.Start.Y;
        double[] p = { -dx, dx, -dy, dy }, q = { s.Start.X - b.Left, b.Right - s.Start.X, s.Start.Y - b.Bottom, b.Top - s.Start.Y };
        for (int i = 0; i < 4; i++)
        {
            if (Math.Abs(p[i]) < 1e-9) { if (q[i] < 0) return false; }
            else if (p[i] < 0) low = Math.Max(low, q[i] / p[i]);
            else high = Math.Min(high, q[i] / p[i]);
        }
        return low <= high;
    }

    private static double Distance(Rect2 a, Rect2 b) => Math.Sqrt(Math.Pow(Math.Max(0, Math.Max(a.Left - b.Right, b.Left - a.Right)), 2) +
        Math.Pow(Math.Max(0, Math.Max(a.Bottom - b.Top, b.Bottom - a.Top)), 2));
    private static bool SourcePreserved(ManifestRecord source, ManifestRecord current)
    {
        if (current.RawText != source.RawText || current.ObjectType != source.ObjectType || current.OwnerPath != source.OwnerPath ||
            JsonSerializer.Serialize(current.Properties) != JsonSerializer.Serialize(source.Properties)) return false;
        bool aligned = source.Properties.HorizontalMode is not "TextLeft";
        if (!aligned) return JsonSerializer.Serialize(current.Geometry) == JsonSerializer.Serialize(source.Geometry);
        return current.Geometry.AlignmentPoint == source.Geometry.AlignmentPoint &&
            current.Geometry.RotationRadians == source.Geometry.RotationRadians;
    }
    private static ObjectId Resolve(Database db, string handle)
    {
        try { return db.GetObjectId(false, new Handle(long.Parse(handle, NumberStyles.HexNumber)), 0); }
        catch (Autodesk.AutoCAD.Runtime.Exception exception)
        { throw new CommandProtocolException("bilingual_source_handle_missing", $"Source handle {handle}: {exception.Message}"); }
    }
    private static BlockTableRecord? OwningBlock(Transaction tx, DBObject value)
    {
        var id = value.OwnerId;
        while (!id.IsNull) { var owner = tx.GetObject(id, OpenMode.ForRead); if (owner is BlockTableRecord block) return block; id = owner.OwnerId; }
        return null;
    }
    internal static string Plain(string raw) { using var text = new MText { Contents = raw }; return text.Text; }
    private static string Normalize(string value) => Regex.Replace(value, @"[\s\p{P}]+", "").ToUpperInvariant()
        .Replace("TPD", "T", StringComparison.Ordinal).Replace("TD", "T", StringComparison.Ordinal);
    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}").Replace("\r", "").Replace("\n", "\\P");
}

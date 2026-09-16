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
        var issues = new List<CadObjectAccess.Issue>();
        var progress = new ImportProgress();
        try { return RunCore(context, issues, progress); }
        catch (System.Exception exception)
        {
            if (!exception.Data.Contains("stage")) exception.Data["stage"] = progress.Stage;
            if (progress.Row is { } row)
            {
                if (!exception.Data.Contains("recordId")) exception.Data["recordId"] = row.RecordId;
                if (exception.Data["handle"] is null) exception.Data["handle"] = row.Handle;
                if (!exception.Data.Contains("objectType")) exception.Data["objectType"] = row.ObjectType;
            }
            throw;
        }
        finally
        {
            try { NativeDrawing.Report(context, "bilingual-native-timing.json", progress.Finish()); }
            catch (System.Exception reportError) { Console.Error.WriteLine($"Timing report: {reportError.Message}"); }
            if (issues.Count > 0)
            {
                try { NativeDrawing.Report(context, "bilingual-object-access.json", new { count = issues.Count, examples = issues.Take(100).ToArray() }); }
                catch (System.Exception reportError) { Console.Error.WriteLine($"Object access report: {reportError.Message}"); }
            }
        }
    }

    private sealed class ImportProgress
    {
        private readonly System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        private readonly Dictionary<string, double> seconds = new();
        private string stage = "preflight";
        private double last;
        internal string Stage
        {
            get => stage;
            set { Accumulate(); stage = value; }
        }
        private void Accumulate()
        {
            double now = watch.Elapsed.TotalSeconds;
            seconds[stage] = seconds.GetValueOrDefault(stage) + now - last;
            last = now;
        }
        internal object Finish() { Accumulate(); return new { totalSeconds = last, phases = seconds }; }
        internal ManifestRecord? Row;
    }

    private static int RunCore(JobContext context, List<CadObjectAccess.Issue> issues, ImportProgress progress)
    {
        context.VerifySourceAndWorkingHashes();
        ManifestRecord[] manifest = NativeDrawing.ReadRows<ManifestRecord>(context.Config.ManifestPath);
        TranslationRecord[] translations = NativeDrawing.ReadRows<TranslationRecord>(context.Config.TranslationPath!);
        var validation = TranslationValidator.ValidateBatch(manifest, translations);
        if (!validation.IsValid)
        {
            var failure = new CommandProtocolException("bilingual_invalid_batch", string.Join(",", validation.Errors.Select(e => e.Code).Distinct()));
            failure.Data["validationErrors"] = validation.Errors.ToArray();
            throw failure;
        }
        var translated = translations.ToDictionary(r => r.RecordId);
        var pairs = new List<Pair>();
        var unresolved = new List<string>();
        var unresolvedDetails = new List<object>();
        var placementLimits = new Dictionary<string, Rect2>();
        var tableCopies = new List<BilingualTableCopy.CopyReceipt>();
        var tableDecisions = new List<object>();
        BilingualTermGroups.Group[] termGroups = [];
        string output = context.Config.OutputPath;
        if (Path.GetFullPath(output).Equals(Path.GetFullPath(context.Config.SourcePath), StringComparison.OrdinalIgnoreCase) ||
            Path.GetFullPath(output).Equals(Path.GetFullPath(context.Config.WorkingPath), StringComparison.OrdinalIgnoreCase))
            throw new CommandProtocolException("unsafe_output", "Bilingual output must be a separate file.");

        progress.Stage = "open-working-drawing";
        using (var db = NativeDrawing.Open(context.Config.WorkingPath))
        {
            var previous = HostApplicationServices.WorkingDatabase;
            try
            {
                HostApplicationServices.WorkingDatabase = db;
                using var tx = db.TransactionManager.StartTransaction();
                var access = new CadObjectAccess(db, tx, issues);
                progress.Stage = "resolve-source-text";
                var inputList = new List<LayoutWriteInput>();
                foreach (var row in manifest)
                {
                    progress.Row = row;
                    string restored = TranslationValidator.RestoreProtectedTokensForOutput(translated[row.RecordId].TranslatedText, row.ProtectedTokens);
                    ObjectId id;
                    try { id = Resolve(db, row.Handle); }
                    catch (CommandProtocolException) when (restored == row.RawText)
                    {
                        // Missing XREF diagnostic rows are exported for audit but have no ObjectId in the host database.
                        // They are safe to omit only when the translation is an exact passthrough.
                        continue;
                    }
                    access.Read<Entity>(id, "resolve-source-text", recordId: row.RecordId, required: true);
                    inputList.Add(new LayoutWriteInput(id, row, restored, false));
                }
                var inputs = inputList.ToArray();
                progress.Row = null;
                progress.Stage = "capture-topology";
                var baseline = DrawingTopologyCapture.Capture(db, tx, inputs, access);
                // Serialized GeometricExtents can contain stale MText column bounds.
                // Use current font metrics before allocating whitespace beside originals.
                progress.Stage = "refresh-text-bounds";
                baseline = baseline with { Definitions = baseline.Definitions.Select(d => d with {
                    Texts = d.Texts.Select(t => {
                        if (access.Read<Entity>(t.ObjectId, "refresh-text-bounds", d.Name, d.Handle, t.RecordId, required: true) is MText mt && CadLayoutGeometry.TryFreshBounds(mt) is { } b)
                            return t with { Source = t.Source with { Bounds = new Rect2(b.MinX, b.MinY, b.MaxX, b.MaxY) } };
                        return t;
                    }).ToArray() }).ToArray() };
                var topology = baseline.Definitions.SelectMany(d => d.Texts).ToDictionary(t => t.RecordId);
                if (context.Config.SourceLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) &&
                    (context.Config.TargetLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase) ||
                     context.Config.TargetLanguage.StartsWith("fr", StringComparison.OrdinalIgnoreCase)))
                    termGroups=BilingualTermGroups.Find(manifest,baseline,access);
                var termMembers=termGroups.SelectMany(g=>g.RecordIds).ToHashSet();
                var termFollowers=termGroups.SelectMany(g=>g.RecordIds.Skip(1)).ToHashSet();
                foreach (var group in termGroups)
                {
                    string target=translated[group.RecordIds[0]].TranslatedText;
                    if (group.RecordIds.Any(id=>translated[id].TranslatedText!=target))
                        throw new CommandProtocolException("bilingual_term_fragmented", $"Translate the complete term '{group.SourceText}' once: {string.Join(",",group.RecordIds)}");
                    var leader=topology[group.RecordIds[0]];
                    var boxes=group.RecordIds.Select(id=>topology[id].Source.Bounds).ToArray();
                    var union=new Rect2(boxes.Min(b=>b.Left),boxes.Min(b=>b.Bottom),boxes.Max(b=>b.Right),boxes.Max(b=>b.Top));
                    topology[group.RecordIds[0]]=leader with { Source=leader.Source with {Bounds=union,Anchor=union.Center} };
                }
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
                progress.Stage = "copy-bilingual-tables";
                var tableSlots = new Dictionary<string,BilingualGroupLayout.Slot>();
                var tableIds = new HashSet<string>();
                var blockedTables = new HashSet<string>();
                var copied = BilingualTableCopy.Apply(db, tx, baseline, inputs.Where(i=>!termMembers.Contains(i.Manifest.RecordId)).ToArray(), occupied, context.Config.TargetLanguage, pairs, tableCopies, tableDecisions, tableSlots, tableIds, blockedTables, access);
                progress.Stage = "plan-table-slots";
                foreach(var slot in BilingualGroupLayout.Plan(db, baseline, inputs.Where(i => !tableIds.Contains(i.Manifest.RecordId) && !copied.Contains(i.Manifest.RecordId) && !termMembers.Contains(i.Manifest.RecordId)).ToArray(), occupied, context.Config.TargetLanguage, access, tableDecisions))
                    tableSlots[slot.Key]=slot.Value;

                progress.Stage = "project-bilingual-boundaries";
                ProjectNearbyBoundaries(definitions, baseline, inputs);

                progress.Stage = "place-bilingual-text";
                foreach (var input in inputs)
                {
                    var row = input.Manifest;
                    progress.Row = row;
                    if (termFollowers.Contains(row.RecordId)) continue;
                    if (copied.Contains(row.RecordId)) continue;
                    if (translated[row.RecordId].TranslatedText == row.PlainText) continue;
                    if (!topology.TryGetValue(row.RecordId, out var source)) { unresolved.Add(row.RecordId); continue; }
                    var original = access.Read<Entity>(input.ObjectId, "place-source", source.DefinitionName, recordId: row.RecordId, required: true)!;
                    if (original is not DBText && original is not MText && original is not Dimension) { unresolved.Add(row.RecordId); continue; }
                    var definition = definitions[source.DefinitionName];
                    string targetText = Plain(input.RestoredText);
                    string normalized = Normalize(targetText);
                    string sourcePlain = Plain(row.RawText);
                    if (normalized.Length == 0) { unresolved.Add(row.RecordId); continue; }
                    if (context.Config.TargetLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase) &&
                        fixedLabels.ExistingEnglishIdBySourceId.TryGetValue(row.RecordId, out string? existingRecordId) &&
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
                        .Where(t => BilingualLabelEquivalence.MatchesNeighbor(sourcePlain, targetText, Plain(rowsByHandle[t.EntityHandle].RawText)))
                        .Where(t => HasSourceLink(access.Read<Entity>(t.ObjectId, "reuse-target", t.DefinitionName, recordId: t.RecordId, required: true)!, row) ||
                            Distance(source.Source.Bounds, t.Source.Bounds) <= source.Source.OriginalTextHeight * 4 ||
                            IsTableRowNeighbor(source.Source.Bounds, t.Source.Bounds, tableGroups[source.DefinitionName]))
                        .Where(t => !pairs.Any(p => p.TargetHandle == t.EntityHandle))
                        .OrderBy(t => Distance(source.Source.Bounds, t.Source.Bounds)).FirstOrDefault();
                    if (existing is not null)
                    {
                        pairs.Add(new(row.RecordId, row.Handle, existing.EntityHandle, Plain(rowsByHandle[existing.EntityHandle].RawText), "existing-neighbor", source.DefinitionName, existing.Source.Bounds, 1));
                        continue;
                    }

                    if(blockedTables.Contains(row.RecordId))
                    {
                        unresolved.Add(row.RecordId);
                        unresolvedDetails.Add(new {row.RecordId,row.Handle,source.Source,
                            reason="table-copy-unresolved",details="See bilingual-table-layout.json; keep the table grouped for local repair."});
                        continue;
                    }
                    var owner = OwningBlock(tx, original, access);
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
                    bool placed = false;
                    if (inTable)
                    {
                        added.TextStyleId = db.Textstyle;
                        added.Contents = BilingualGroupLayout.Contents(slot.DisplayText, context.Config.TargetLanguage);
                        added.TextHeight = slot.Height;
                        added.Width = slot.WrapWidth;
                        var footprint = BilingualPlacementChecks.Footprint(added);
                        BilingualPlacementChecks.Move(added, footprint, slot.Bounds.Left, slot.Bounds.Top, row.Geometry.InsertionPoint.Z);
                        bounds = new Rect2(slot.Bounds.Left, slot.Bounds.Top-footprint.Height, slot.Bounds.Left+footprint.Width, slot.Bounds.Top);
                        placed = slot.Bounds.Contains(bounds, 1e-5);
                    }
                    if (placed) scale = slot.Height / source.Source.OriginalTextHeight;
                    if(inTable && !placed && slot.Strategy=="table-inline-right")
                    {
                        unresolved.Add(row.RecordId);
                        unresolvedDetails.Add(new {row.RecordId,row.Handle,reason="table-inline-measurement-changed",slot.Bounds});
                        continue;
                    }
                    var placementTrace = new BilingualPlacementTrace();
                    if (!placed && !Place(added, contents, source, definition, occupied[source.DefinitionName], row.Geometry.InsertionPoint.Z, out bounds, out scale, placementTrace))
                    { unresolved.Add(row.RecordId); unresolvedDetails.Add(new { row.RecordId, row.Handle, source.Source,
                        source.Region, actualWidth = added.ActualWidth, actualHeight = added.ActualHeight,
                        placement = placementTrace.Report(baseline, pairs, source.DefinitionName),
                        nearbyText = definition.Texts.Where(t => Distance(source.Source.Bounds, t.Source.Bounds) < source.Source.OriginalTextHeight * 4)
                            .Select(t => new { t.EntityHandle, text = rowsByHandle.GetValueOrDefault(t.EntityHandle)?.RawText, t.Source.Bounds }),
                        occupied = occupied[source.DefinitionName].Where(b => Distance(source.Source.Bounds, b) < source.Source.OriginalTextHeight * 4).Distinct().Take(60),
                        instances = baseline.BlockInstances.Where(i => i.DefinitionId == source.DefinitionName)
                            .Select(i => new { i.Path, bounds = i.WorldTransform.Apply(source.Source.Bounds) }) }); continue; }
                    owner.UpgradeOpen();
                    placementLimits[row.RecordId] = placed && inTable ? slot.Bounds : placementTrace.Allowed;
                    owner.AppendEntity(added);
                    tx.AddNewlyCreatedDBObject(added, true);
                    LinkSource(db, tx, added, row);
                    ReserveProjected(occupied, source.DefinitionName, bounds, baseline.BlockInstances);
                    pairs.Add(new(row.RecordId, row.Handle, added.Handle.ToString(), targetText, "added", source.DefinitionName, bounds, scale, placed && inTable ? slot.Strategy : "cell-local-or-nearby"));
                }
                foreach (var group in termGroups)
                {
                    var leader=pairs.FirstOrDefault(p=>p.RecordId==group.RecordIds[0]);
                    foreach (string id in group.RecordIds.Skip(1))
                    {
                        if (leader is null) { unresolved.Add(id); continue; }
                        var member=topology[id];
                        pairs.Add(leader with {RecordId=id,SourceHandle=member.EntityHandle,Decision="group-member",PlacementStrategy="complete-term"});
                    }
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
                progress.Row = null;
                progress.Stage = "save-bilingual-candidate";
                tx.Commit();
                NativeDrawing.Save(db, output);
            }
            finally { HostApplicationServices.WorkingDatabase = previous; }
        }

        progress.Stage = "verify-saved-candidate";
        NativeDrawing.Report(context, "bilingual-pairs.json", new { outputMode = "bilingual", pairs, unresolved });
        NativeDrawing.Report(context, "bilingual-term-placement.json", new {groups=termGroups});
        NativeDrawing.Report(context, "bilingual-unresolved.json", unresolvedDetails);
        NativeDrawing.Report(context, "bilingual-table-layout.json", new { tables = tableDecisions, copies = tableCopies });
        NativeDrawing.Report(context, "bilingual-placement-limits.json", placementLimits);
        return VerifySaved(context, manifest, translated, pairs, unresolved, tableCopies, placementLimits, issues);
    }

    internal static int VerifySaved(JobContext context, ManifestRecord[] manifest,
        IReadOnlyDictionary<string, TranslationRecord> translated, IReadOnlyList<Pair> pairs,
        IReadOnlyCollection<string> unresolved, IReadOnlyList<BilingualTableCopy.CopyReceipt> tableCopies,
        IReadOnlyDictionary<string, Rect2> placementLimits, List<CadObjectAccess.Issue> issues)
    {
        string output = context.Config.OutputPath;
        // Reopen saved DWG before proving source retention and target associations.
        ManifestRecord[] candidate = [];
        BilingualSavedLayoutReview.Risk[] layoutRisks = [];
        NativeDrawing.ExportCandidate(context, db => {
            candidate = NativeDrawing.ReadRows<ManifestRecord>(Path.Combine(context.Config.ArtifactDirectory, "bilingual-candidate.jsonl"));
            layoutRisks = BilingualSavedLayoutReview.MeasureAndInspect(db, pairs, candidate, placementLimits, issues);
        });
        var candidateByHandle = candidate.ToDictionary(r => r.Handle, StringComparer.OrdinalIgnoreCase);
        var changedSources = manifest.Where(r => !candidateByHandle.TryGetValue(r.Handle, out var current)
                ? translated[r.RecordId].TranslatedText != r.FormatTemplate
                : !SourcePreserved(r, current)).Select(r => r.RecordId).ToArray();
        var missingTargets = pairs.Where(p => !candidateByHandle.TryGetValue(p.TargetHandle, out var target) ||
            (p.Decision == "existing-inline"
                ? !Normalize(Plain(target.RawText)).Contains(Normalize(p.TargetText), StringComparison.Ordinal)
                : Normalize(Plain(target.RawText)) != Normalize(p.TargetText))).Select(p => p.RecordId).ToArray();
        BilingualTableCopy.Verify(context, tableCopies);
        DrawingVerifier.VerifyStructure(context, "bilingual-structure.json", tableCopies.Select(c => c.TargetHandle).ToHashSet(StringComparer.OrdinalIgnoreCase));
        bool passed = changedSources.Length == 0 && missingTargets.Length == 0 && unresolved.Count == 0;
        NativeDrawing.Report(context, "bilingual-native-check.json", new { status = passed ? "passed" : "failed", outputMode = "bilingual",
            sourceRetainedCount = manifest.Length - changedSources.Length, changedSources, missingTargets, unresolved,
            addedCount = pairs.Count(p => p.Decision == "added"), skippedExistingCount = pairs.Count(p => p.Decision != "added" && p.Decision != "group-member"),
            groupedSourceCount = pairs.Count(p=>p.Decision=="group-member"),
            candidateSha256 = Hashing.Sha256File(output), sourceSha256 = context.Config.SourceSha256 });
        NativeDrawing.Report(context, "bilingual-layout-audit.json", new { candidateReopened = true,
            texts = pairs, risks = layoutRisks, manualReview = unresolved,
            reviewRecordIds = layoutRisks.Select(r => r.RecordId).Distinct().ToArray(),
            auditScope = "Saved local-coordinate text bounds, placement-region containment and size; bounding-box findings require visual assessment. Cross-instance association and protected geometry still require placed-world visual review; not a complete collision proof.",
            riskCounts = new { low = 0, medium = layoutRisks.Length, high = unresolved.Count } });
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


    private static void ProjectNearbyBoundaries(Dictionary<string, CadDefinitionTopology> definitions,
        CadLayoutBaseline baseline, IReadOnlyList<LayoutWriteInput> inputs)
    {
        var changed = inputs.Where(i => i.RestoredText != i.Manifest.RawText).Select(i => i.Manifest.RecordId).ToHashSet();
        foreach (var definition in baseline.Definitions)
        {
            var scopes = definition.Texts.Where(t => changed.Contains(t.RecordId)).Select(t => {
                var b=t.Source.Bounds; double margin=t.Source.OriginalTextHeight*16;
                return definition.Regions.Where(r => r.Kind is LayoutRegionKind.TableCell or LayoutRegionKind.ClosedFrame)
                    .Where(r => r.Bounds.Contains(b)).OrderBy(r => r.Bounds.Area).FirstOrDefault()?.Bounds
                    ?? new Rect2(b.Left-margin,b.Bottom-margin,b.Right+margin,b.Top+margin);
            }).ToArray();
            if (scopes.Length==0) continue;
            // Project actual segments, not filled block bounds. Compute once and
            // retain only this definition's label-search regions, not the entire drawing.
            var foreign = baseline.Definitions.Where(d => d.Name!=definition.Name)
                .SelectMany(d => InstanceOccupancyProjection.ProjectSegments(d.BoundarySegments,d.Name,definition.Name,baseline.BlockInstances))
                .Where(s => scopes.Any(b => s.MaxX>=b.Left && s.MinX<=b.Right && s.MaxY>=b.Bottom && s.MinY<=b.Top));
            definitions[definition.Name]=definition with { BoundarySegments=definition.BoundarySegments.Concat(foreign).Distinct().ToArray() };
        }
    }

    private static void ReserveProjected(Dictionary<string, List<Rect2>> occupied, string definition, Rect2 bounds,
        IReadOnlyList<BlockInstancePath> instances, bool includeLocal = true)
    {
        if (includeLocal) occupied[definition].Add(bounds);
        foreach (string target in occupied.Keys.Where(name => name != definition))
            occupied[target].AddRange(InstanceOccupancyProjection.Project(bounds, definition, target, instances));
    }

    private static bool Place(MText text, string contents, CadLayoutText source, CadDefinitionTopology definition,
        List<Rect2> occupied, double z, out Rect2 result, out double usedScale, BilingualPlacementTrace? trace = null)
    {
        Rect2 box = source.Source.Bounds;
        double height = source.Source.OriginalTextHeight;
        var container = definition.Regions.Where(r => r.Kind is LayoutRegionKind.TableCell or LayoutRegionKind.ClosedFrame)
            .Where(r => r.Bounds.Contains(box, height * .05)).OrderBy(r => r.Bounds.Area).FirstOrDefault();
        Rect2 allowed = container is null
            ? new Rect2(box.Left - height * 16, box.Bottom - height * 16, box.Right + height * 16, box.Top + height * 16)
            : BilingualPlacementPolicy.AllowedForTightRotatedLabel(container.Bounds, box, height, text.Rotation,
                container.Kind == LayoutRegionKind.TableCell);
        if (trace is not null) { trace.Allowed = allowed; trace.RegionId = container?.Id; }
        // Every candidate must be inside allowed. Distant text cannot collide;
        // filter once instead of scanning every block's text for every trial.
        occupied = occupied.Where(o => Intersects(allowed, o, height * .12)).Distinct().ToList();
        definition = definition with {
            BoundarySegments = definition.BoundarySegments.Where(s => s.MaxX>=allowed.Left-1e-6 &&
                s.MinX<=allowed.Right+1e-6 && s.MaxY>=allowed.Bottom-1e-6 && s.MinY<=allowed.Top+1e-6).ToArray(),
            ProtectedGeometry = definition.ProtectedGeometry.Where(g => Intersects(allowed,g.Bounds,1e-6)).ToArray()
        };
        if (TryReadableNearby(text, contents, source, definition, allowed, occupied, z, out result, out usedScale, trace)) return true;
        // Exhaust readable wrapping in adjacent pockets before the legacy search
        // can accept a distant single line at one particular scale.
        if (TryCloseLabel(text, contents, source, definition, allowed, occupied, z, out result, out usedScale, trace)) return true;
        if (TryNearbyGrid(text, contents, source, definition, allowed, occupied, z, out result, out usedScale, trace)) return true;
        foreach (double scale in BilingualPlacementPolicy.HeightScales)
        foreach (double widthFactor in new[] { 1.0, .8, .65, .5, .4 })
        {
            text.TextHeight = height * scale;
            text.Width = 0;
            text.Contents = "{\\W" + widthFactor.ToString(CultureInfo.InvariantCulture) + ";" + contents + "}";
            double unwrappedWidth = Math.Max(text.TextHeight, text.ActualWidth) * 1.05;
            foreach (double width in BilingualPlacementPolicy.CandidateWidths(BilingualPlacementChecks.AlongText(allowed, text.Rotation), BilingualPlacementChecks.AlongText(box, text.Rotation), height, unwrappedWidth))
            {
            text.Width = Math.Max(height, width);
            Rect2 footprint = BilingualPlacementChecks.Footprint(text);
            double w = footprint.Width, h = footprint.Height;
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
                    BilingualPlacementChecks.Move(text, footprint, point.X, point.Y, z);
                    var proposed = new Rect2(point.X, point.Y - h, point.X + w, point.Y);
                    if (!BilingualPlacementChecks.Accept(text, proposed, allowed, source, definition, occupied, height * .12, trace, out Rect2 bounds)) continue;
                    result = bounds; usedScale = scale; return true;
                }
            }
            }
        }
        // One bounded obstacle-edge search before the existing emergency strategy.
        if (TryLocalWhitespace(text, contents, source, definition, allowed, occupied, z, out result, out usedScale, trace)) return true;
        // Emergency candidates still use the normal containment/geometry checks.
        {
            double scale = LayoutFitPolicy.EmergencyMinimumHeightScale;
            text.TextHeight = height * scale;
            text.Width = Math.Max(height, Math.Min(allowed.Width, height * 4));
            text.Contents = "{\\W0.4;" + contents + "}";
            Rect2 footprint = BilingualPlacementChecks.Footprint(text);
            double w = footprint.Width, h = footprint.Height;
            double margin = Math.Min(height * .035, Math.Min(allowed.Width, allowed.Height) * .04);
            var otherText = occupied.ToArray();
            foreach (Rect2 fallback in BilingualPlacementPolicy.EmergencyCandidates(allowed, box, w, h, margin))
            {
                BilingualPlacementChecks.Move(text, footprint, fallback.Left, fallback.Top, z);
                if (!BilingualPlacementChecks.Accept(text, fallback, allowed, source, definition, otherText, margin, trace, out result)) continue;
                usedScale = scale;
                return true;
            }
            if (container is not null && w + margin * 2 <= allowed.Width && h + margin * 2 <= allowed.Height)
            {
                Rect2 cellBottom = BilingualPlacementPolicy.PlaceAtCellBottom(allowed, w, h, margin);
                BilingualPlacementChecks.Move(text, footprint, cellBottom.Left, cellBottom.Top, z);
                if (BilingualPlacementChecks.Accept(text, cellBottom, allowed, source, definition, otherText, margin, trace, out result))
                {
                    usedScale = scale;
                    return true;
                }
            }
        }
        result = box; usedScale = 0; return false;
    }

    internal static bool TryReadableNearby(MText text, string contents, CadLayoutText source,
        CadDefinitionTopology definition, Rect2 allowed, IReadOnlyList<Rect2> occupied, double z,
        out Rect2 result, out double usedScale, BilingualPlacementTrace? trace = null)
    {
        result = source.Source.Bounds; usedScale = 0;
        if (!text.Normal.IsEqualTo(Vector3d.ZAxis)) return false;
        Rect2 box = source.Source.Bounds;
        double height = source.Source.OriginalTextHeight;
        // At most 16 unwrapped, normal-width trials. Do not multiply the dense
        // drawing's existing width/height/wrap search or move the source text.
        foreach (double scale in new[] {1.0, .7})
        {
            text.TextHeight = height * scale; text.Width = 0; text.Contents = "{\\W1;" + contents + "}";
            Rect2 footprint = BilingualPlacementChecks.Footprint(text);
            double w = footprint.Width, h = footprint.Height;
            if (!(w > 0 && h > 0) || w > allowed.Width || h > allowed.Height) continue;
            foreach (double gap in new[] {height * .12, height * .77})
            foreach (Point3d point in new[] {
                new Point3d(box.Center.X - w / 2, box.Bottom - gap, z),
                new Point3d(box.Center.X - w / 2, box.Top + gap + h, z),
                new Point3d(box.Right + gap, box.Top, z),
                new Point3d(box.Left - gap - w, box.Top, z) })
            {
                BilingualPlacementChecks.Move(text, footprint, point.X, point.Y, z);
                var proposed = new Rect2(point.X, point.Y - h, point.X + w, point.Y);
                if (!BilingualPlacementChecks.Accept(text, proposed, allowed, source, definition, occupied, height * .12, trace, out Rect2 actual)) continue;
                result = actual; usedScale = scale; return true;
            }
        }
        return false;
    }

    private static bool TryCloseLabel(MText text, string contents, CadLayoutText source,
        CadDefinitionTopology definition, Rect2 allowed, IReadOnlyList<Rect2> occupied, double z,
        out Rect2 result, out double usedScale, BilingualPlacementTrace? trace)
    {
        result=source.Source.Bounds; usedScale=0;
        if (!text.Normal.IsEqualTo(Vector3d.ZAxis)) return false;
        var box=source.Source.Bounds;
        double height=source.Source.OriginalTextHeight;
        double along=BilingualPlacementChecks.AlongText(box,text.Rotation);
        // Geometry edges seed positions as well as vetoing them. Hints are not
        // filled obstacles: the existing segment/geometry checks still decide clearance.
        var hints=definition.BoundarySegments
            .Where(s=>s.MaxX>=allowed.Left && s.MinX<=allowed.Right && s.MaxY>=allowed.Bottom && s.MinY<=allowed.Top)
            .Select(s=>new Rect2(s.MinX,s.MinY,s.MaxX,s.MaxY))
            .Concat(definition.ProtectedGeometry.Where(g=>!g.Bounds.Contains(box)).Select(g=>g.Bounds)).Distinct()
            .OrderBy(b=>BilingualLocalPlacement.Gap(box,b)).Take(24).ToArray();
        bool Clear(Rect2 b) => !definition.BoundarySegments.Any(s=>Crosses(b,s)) &&
            !definition.ProtectedGeometry.Any(g=>!g.Bounds.Contains(box) && Intersects(b,g.Bounds,0));
        foreach (var pass in new[] {(Reach:.8,Small:false),(Reach:2.0,Small:false),(Reach:2.0,Small:true)})
        foreach (double scale in pass.Small ? new[] {.30,.25} : new[] {.7,.55,.45,.35})
        foreach (double factor in new[] {1.0,.8})
        {
            text.TextHeight=height*scale;
            text.Contents="{\\W"+factor.ToString(CultureInfo.InvariantCulture)+";"+contents+"}";
            foreach (double width in new[] {0, Math.Max(height,along-height*.24),
                Math.Max(height,along*.8), Math.Max(height,along*1.6),
                Math.Max(height,along*.5-height*.24), Math.Max(height,along*.35-height*.24)}.Distinct())
            {
                text.Width=width;
                var footprint=BilingualPlacementChecks.Footprint(text);
                foreach (var slot in BilingualLocalPlacement.Candidates(allowed,box,
                    footprint.Width,footprint.Height,occupied,height*.12,hints,Clear)
                    .Where(b=>BilingualLocalPlacement.Gap(box,b)<=height*pass.Reach)
                    .OrderBy(b=>BilingualLocalPlacement.Gap(box,b)).Take(32))
                {
                    BilingualPlacementChecks.Move(text,footprint,slot.Left,slot.Top,z);
                    if (!BilingualPlacementChecks.Accept(text,slot,allowed,source,definition,occupied,height*.12,trace,out var actual)) continue;
                    if (BilingualLocalPlacement.Gap(box,actual)>height*pass.Reach) continue;
                    result=actual; usedScale=scale; return true;
                }
            }
        }
        return false;
    }

    private static bool TryNearbyGrid(MText text, string contents, CadLayoutText source,
        CadDefinitionTopology definition, Rect2 allowed, IReadOnlyList<Rect2> occupied, double z,
        out Rect2 result, out double usedScale, BilingualPlacementTrace? trace)
    {
        result=source.Source.Bounds; usedScale=0;
        if (!text.Normal.IsEqualTo(Vector3d.ZAxis)) return false;
        var box=source.Source.Bounds; double height=source.Source.OriginalTextHeight, reach=height*4;
        double along=BilingualPlacementChecks.AlongText(box,text.Rotation);
        foreach(double scale in new[]{.45,.35,.30,.25})
        foreach(double width in new[]{along,along*.5,along*.35}.Select(w=>Math.Max(height,w)).Distinct())
        {
            text.TextHeight=height*scale; text.Width=width; text.Contents="{\\W0.8;"+contents+"}";
            var fp=BilingualPlacementChecks.Footprint(text);
            double left=Math.Max(allowed.Left,box.Left-reach-fp.Width), right=Math.Min(allowed.Right-fp.Width,box.Right+reach);
            double bottom=Math.Max(allowed.Bottom,box.Bottom-reach-fp.Height), top=Math.Min(allowed.Top-fp.Height,box.Top+reach);
            if(right<left || top<bottom) continue;
            int columns=Math.Clamp((int)Math.Ceiling((right-left)/(height*.5)),1,32);
            int rows=Math.Clamp((int)Math.Ceiling((top-bottom)/(height*.5)),1,32);
            // At most 1089 positions per measured layout, only after edge-based
            // nearby placement failed. Prune known collisions before native measurement.
            var candidates=Enumerable.Range(0,columns+1).SelectMany(x=>Enumerable.Range(0,rows+1).Select(y=>
                new Rect2(left+(right-left)*x/columns,bottom+(top-bottom)*y/rows,
                    left+(right-left)*x/columns+fp.Width,bottom+(top-bottom)*y/rows+fp.Height)))
                .Where(b=>BilingualLocalPlacement.Gap(box,b)<=reach && !occupied.Any(o=>Intersects(b,o,height*.12)) &&
                    !definition.BoundarySegments.Any(s=>Crosses(b,s)) &&
                    !definition.ProtectedGeometry.Any(g=>!g.Bounds.Contains(box) && Intersects(b,g.Bounds,0)))
                .OrderBy(b=>BilingualLocalPlacement.Gap(box,b)).ThenBy(b=>Math.Pow(b.Center.X-box.Center.X,2)+Math.Pow(b.Center.Y-box.Center.Y,2));
            foreach(var candidate in candidates.Take(32))
            {
                BilingualPlacementChecks.Move(text,fp,candidate.Left,candidate.Top,z);
                if(!BilingualPlacementChecks.Accept(text,candidate,allowed,source,definition,occupied,height*.12,trace,out var actual)) continue;
                if(BilingualLocalPlacement.Gap(box,actual)>reach) continue;
                result=actual;usedScale=scale;return true;
            }
        }
        return false;
    }

    internal static bool TryLocalWhitespace(MText text, string contents, CadLayoutText source,
        CadDefinitionTopology definition, Rect2 allowed, IReadOnlyList<Rect2> occupied, double z,
        out Rect2 result, out double usedScale, BilingualPlacementTrace? trace = null)
    {
        result=source.Source.Bounds; usedScale=0;
        if (!text.Normal.IsEqualTo(Vector3d.ZAxis)) return false;
        double height=source.Source.OriginalTextHeight;
        foreach (double scale in new[] {.35,.25})
        foreach (double factor in new[] {.8,.5})
        {
            text.TextHeight=height*scale; text.Width=0;
            text.Contents="{\\W"+factor.ToString(CultureInfo.InvariantCulture)+";"+contents+"}";
            double unwrapped=Math.Max(text.TextHeight,text.ActualWidth)*1.05;
            foreach (double width in BilingualPlacementPolicy.CandidateWidths(BilingualPlacementChecks.AlongText(allowed,text.Rotation),BilingualPlacementChecks.AlongText(source.Source.Bounds,text.Rotation),height,unwrapped))
            {
                text.Width=width;
                Rect2 footprint=BilingualPlacementChecks.Footprint(text);
                double w=footprint.Width, h=footprint.Height;
                foreach (Rect2 slot in BilingualLocalPlacement.Candidates(allowed,source.Source.Bounds,w,h,occupied,height*.12))
                {
                    BilingualPlacementChecks.Move(text,footprint,slot.Left,slot.Top,z);
                    if (!BilingualPlacementChecks.Accept(text,slot,allowed,source,definition,occupied,height*.12,trace,out Rect2 actual)) continue;
                    result=actual; usedScale=scale; return true;
                }
            }
        }
        return false;
    }

    internal static bool Intersects(Rect2 a, Rect2 b, double padding) =>
        a.Right > b.Left - padding && a.Left < b.Right + padding && a.Top > b.Bottom - padding && a.Bottom < b.Top + padding;

    internal static bool Crosses(Rect2 b, Segment2 s)
    {
        // Most segments are outside this candidate. Reject them without allocating
        // clipping arrays; projected neighboring blocks can contribute many lines.
        if (s.MaxX<b.Left || s.MinX>b.Right || s.MaxY<b.Bottom || s.MinY>b.Top) return false;
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
        if (current.RawText != source.RawText || current.ObjectType != source.ObjectType ||
            NonTextStructureSignaturePolicy.StableOwnerPath(current.OwnerPath) != NonTextStructureSignaturePolicy.StableOwnerPath(source.OwnerPath) ||
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
        { throw new CommandProtocolException("bilingual_source_handle_missing", $"Source handle {handle}: {exception.Message}", exception); }
    }
    private static BlockTableRecord? OwningBlock(Transaction tx, DBObject value, CadObjectAccess access)
    {
        var id = value.OwnerId;
        var visited = new HashSet<ObjectId>();
        while (!id.IsNull && visited.Add(id))
        {
            var owner = access.Read<DBObject>(id, "source-owner", parentHandle: value.Handle.ToString());
            if (owner is null) return null;
            if (owner is BlockTableRecord block) return block;
            id = owner.OwnerId;
        }
        return null;
    }
    internal static string Plain(string raw) { using var text = new MText { Contents = raw }; return text.Text; }
    internal static string Normalize(string value) => Regex.Replace(value, @"[\s\p{P}]+", "").ToUpperInvariant()
        .Replace("TPD", "T", StringComparison.Ordinal).Replace("TD", "T", StringComparison.Ordinal);
    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}").Replace("\r", "").Replace("\n", "\\P");
}

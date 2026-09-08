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
    internal sealed record Pair(string RecordId, string SourceHandle, string TargetHandle,
        string TargetText, string Decision, string DefinitionName, Rect2 Bounds, double HeightScale);

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
                var inputs = manifest.Select(row => new LayoutWriteInput(Resolve(db, row.Handle), row,
                    TranslationValidator.RestoreProtectedTokensForOutput(translated[row.RecordId].TranslatedText, row.ProtectedTokens), false)).ToArray();
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
                var occupied = baseline.Definitions.ToDictionary(d => d.Name, d => d.Texts.Select(t => t.Source.Bounds).ToList());
                var rowsByHandle = manifest.ToDictionary(r => r.Handle, StringComparer.OrdinalIgnoreCase);

                foreach (var input in inputs)
                {
                    var row = input.Manifest;
                    if (translated[row.RecordId].TranslatedText == row.PlainText) continue;
                    if (!topology.TryGetValue(row.RecordId, out var source)) { unresolved.Add(row.RecordId); continue; }
                    var original = (Entity)tx.GetObject(input.ObjectId, OpenMode.ForRead);
                    // Dimensions need a leader-aware placement policy, not a bounding-box guess.
                    if (original is not DBText && original is not MText) { unresolved.Add(row.RecordId); continue; }
                    var definition = definitions[source.DefinitionName];
                    string targetText = Plain(input.RestoredText);
                    string normalized = Normalize(targetText);
                    string sourcePlain = Plain(row.RawText);
                    if (normalized.Length == 0) { unresolved.Add(row.RecordId); continue; }
                    if (Normalize(sourcePlain).Contains(normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        pairs.Add(new(row.RecordId, row.Handle, row.Handle, targetText, "existing-inline", source.DefinitionName, source.Source.Bounds, 1));
                        continue;
                    }
                    var existing = definition.Texts.Where(t => t.RecordId != row.RecordId && rowsByHandle.ContainsKey(t.EntityHandle))
                        .Where(t => Normalize(Plain(rowsByHandle[t.EntityHandle].RawText)) == normalized)
                        .Where(t => Distance(source.Source.Bounds, t.Source.Bounds) <= source.Source.OriginalTextHeight * 4)
                        .Where(t => !pairs.Any(p => p.TargetHandle == t.EntityHandle))
                        .OrderBy(t => Distance(source.Source.Bounds, t.Source.Bounds)).FirstOrDefault();
                    if (existing is not null)
                    {
                        pairs.Add(new(row.RecordId, row.Handle, existing.EntityHandle, targetText, "existing-neighbor", source.DefinitionName, existing.Source.Bounds, 1));
                        continue;
                    }

                    var owner = OwningBlock(tx, original);
                    if (owner is null || owner.IsFromExternalReference) { unresolved.Add(row.RecordId); continue; }
                    using var added = new MText();
                    added.SetDatabaseDefaults(db);
                    added.LayerId = original.LayerId;
                    added.Color = original.Color;
                    added.TextStyleId = original is MText mt ? mt.TextStyleId : ((DBText)original).TextStyleId;
                    added.Attachment = AttachmentPoint.TopLeft;
                    added.Normal = original is MText plane ? plane.Normal : ((DBText)original).Normal;
                    added.Rotation = row.Geometry.RotationRadians;
                    string contents = Escape(targetText);
                    if (context.Config.TargetLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) contents = @"\FSimSun;" + contents;
                    if (!Place(added, contents, source, definition, occupied[source.DefinitionName], row.Geometry.InsertionPoint.Z, out var bounds, out double scale))
                    { unresolved.Add(row.RecordId); unresolvedDetails.Add(new { row.RecordId, row.Handle, source.Source,
                        source.Region, actualWidth = added.ActualWidth, actualHeight = added.ActualHeight }); continue; }
                    owner.UpgradeOpen();
                    owner.AppendEntity(added);
                    tx.AddNewlyCreatedDBObject(added, true);
                    occupied[source.DefinitionName].Add(bounds);
                    pairs.Add(new(row.RecordId, row.Handle, added.Handle.ToString(), targetText, "added", source.DefinitionName, bounds, scale));
                }
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
        var changedSources = manifest.Where(r => !candidateByHandle.TryGetValue(r.Handle, out var current) ||
            current.RawText != r.RawText || current.ObjectType != r.ObjectType || current.OwnerPath != r.OwnerPath ||
            JsonSerializer.Serialize(current.Properties) != JsonSerializer.Serialize(r.Properties) ||
            JsonSerializer.Serialize(current.Geometry) != JsonSerializer.Serialize(r.Geometry)).Select(r => r.RecordId).ToArray();
        var missingTargets = pairs.Where(p => !candidateByHandle.TryGetValue(p.TargetHandle, out var target) ||
            !Normalize(Plain(target.RawText)).Contains(Normalize(p.TargetText), StringComparison.OrdinalIgnoreCase)).Select(p => p.RecordId).ToArray();
        DrawingVerifier.VerifyStructure(context, "bilingual-structure.json");
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

    private static bool Place(MText text, string contents, CadLayoutText source, CadDefinitionTopology definition,
        List<Rect2> occupied, double z, out Rect2 result, out double usedScale)
    {
        Rect2 box = source.Source.Bounds;
        double height = source.Source.OriginalTextHeight;
        var container = definition.Regions.Where(r => r.Kind is LayoutRegionKind.TableCell or LayoutRegionKind.ClosedFrame)
            .Where(r => r.Bounds.Contains(box, height * .05)).OrderBy(r => r.Bounds.Area).FirstOrDefault();
        Rect2 allowed = container?.Bounds ?? new Rect2(box.Left - height * 16, box.Bottom - height * 16, box.Right + height * 16, box.Top + height * 16);
        foreach (double scale in new[] { .75, .65, .55, .45, .35 })
        foreach (double widthFactor in new[] { 1.0, .8, .65, .5, .4 })
        foreach (double width in new[] { Math.Min(allowed.Width, Math.Max(box.Width, height * 4)), Math.Min(allowed.Width, Math.Max(box.Width * 1.6, height * 8)) }.Distinct())
        {
            text.TextHeight = height * scale;
            text.Width = Math.Max(height, width);
            text.Contents = "{\\W" + widthFactor.ToString(CultureInfo.InvariantCulture) + ";" + contents + "}";
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
                    if (!allowed.Contains(bounds, 1e-6) || occupied.Any(o => Intersects(bounds, o, height * .035))) continue;
                    if (definition.BoundarySegments.Any(line => Crosses(bounds, line))) continue;
                    if (definition.ProtectedGeometry.Any(g => !g.Bounds.Contains(box) && Intersects(bounds, g.Bounds, 0))) continue;
                    result = bounds; usedScale = scale; return true;
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
    private static ObjectId Resolve(Database db, string handle) => db.GetObjectId(false, new Handle(long.Parse(handle, NumberStyles.HexNumber)), 0);
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

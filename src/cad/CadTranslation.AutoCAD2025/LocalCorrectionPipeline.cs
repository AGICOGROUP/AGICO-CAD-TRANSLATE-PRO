using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadTranslation.Core;
using CadTranslation.Contracts;

namespace CadTranslation.AutoCAD2025;

// One transaction for AI-selected layout edits; text and the prior candidate stay intact.
internal static class LocalCorrectionPipeline
{
    private sealed record Correction(string CandidateSha256, Edit[] Edits);
    private sealed record Edit(string Handle, string ExpectedText, double? Width, double? Height, double? X, double? Y);

    internal static int Run(JobContext context)
    {
        string input = context.Config.CandidatePath ?? throw new CommandProtocolException("missing_candidate", "correct requires candidatePath.");
        CandidateInspection.RequireCandidate(context, input);
        if (File.Exists(context.Config.OutputPath)) throw new CommandProtocolException("existing_output", "Correction requires a fresh output path.");
        var corrections = JsonSerializer.Deserialize<Correction>(File.ReadAllText(context.Config.CorrectionPath!), JsonDefaults.Options)
            ?? throw new CommandProtocolException("invalid_corrections", "Corrections must be a JSON object.");
        if (corrections.CandidateSha256 != context.Config.CandidateSha256 || corrections.Edits is not { Length: > 0 } ||
            corrections.Edits.Select(e => e.Handle).Distinct(StringComparer.OrdinalIgnoreCase).Count() != corrections.Edits.Length)
            throw new CommandProtocolException("invalid_corrections", "Require matching candidate hash and unique nonempty edits.");

        // Prove the input before using its associations as edit authorization.
        CandidateInspection.Run(context.Derive(context.Config with { OutputPath = input }));
        var sourceHandles = NativeDrawing.ReadRows<CadTranslation.Contracts.ManifestRecord>(context.Config.ManifestPath)
            .Select(r => r.Handle).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowed = context.Config.OutputMode == "bilingual"
            ? CandidateInspection.Read<CandidateInspection.PairsReceipt>(context, "bilingual-pairs.json").Pairs
                .Where(p => !sourceHandles.Contains(p.TargetHandle)).Select(p => p.TargetHandle).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : CandidateInspection.CandidateRows(context).Where(r => r.ObjectType is "AcDbText" or "AcDbMText")
                .Select(r => r.Handle).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changes = new List<object>();
        using (var db = NativeDrawing.Open(input))
        {
            var previous = HostApplicationServices.WorkingDatabase;
            try
            {
                HostApplicationServices.WorkingDatabase = db;
                using var tx = db.TransactionManager.StartTransaction();
                foreach (var edit in corrections.Edits)
                {
                    if (!allowed.Contains(edit.Handle)) throw new CommandProtocolException("correction_target_forbidden", $"Not an editable translation target: {edit.Handle}");
                    if (new[] { edit.Width, edit.Height, edit.X, edit.Y }.Where(v => v.HasValue).Any(v => !double.IsFinite(v!.Value)) ||
                        edit.Width is <= 0 || edit.Height is <= 0 || (edit.X.HasValue != edit.Y.HasValue) ||
                        !(edit.Width.HasValue || edit.Height.HasValue || edit.X.HasValue))
                        throw new CommandProtocolException("invalid_correction_geometry", $"Require finite positive sizes and paired x/y: {edit.Handle}");
                    var id = db.GetObjectId(false, new Handle(Convert.ToInt64(edit.Handle, 16)), 0);
                    var entity = (Entity)tx.GetObject(id, OpenMode.ForWrite);
                    string text = entity is MText mt ? mt.Contents : entity is DBText dt ? dt.TextString
                        : throw new CommandProtocolException("unsupported_correction_target", "Only DBText and MText layout edits are supported.");
                    if (text != edit.ExpectedText) throw new CommandProtocolException("correction_text_changed", $"Expected text differs: {edit.Handle}");
                    var before = CadLayoutGeometry.TryBounds(entity);
                    if (entity is MText m)
                    {
                        if (edit.Width is double width) m.Width = width;
                        if (edit.Height is double height) m.TextHeight = height;
                        if (edit.X is double x) m.Location = new Point3d(x, edit.Y!.Value, m.Location.Z);
                    }
                    else if (entity is DBText d)
                    {
                        if (edit.Width.HasValue) throw new CommandProtocolException("unsupported_dbtext_width", "DBText supports height and position; width means an MText boundary width.");
                        if (edit.Height is double height) d.Height = height;
                        if (edit.X is double x)
                        {
                            var point = d.IsDefaultAlignment ? d.Position : d.AlignmentPoint;
                            d.TransformBy(Matrix3d.Displacement(new Vector3d(x-point.X, edit.Y!.Value-point.Y, 0)));
                        }
                        d.AdjustAlignment(db);
                    }
                    var after = entity is MText fresh ? CadLayoutGeometry.TryFreshBounds(fresh) : CadLayoutGeometry.TryBounds(entity);
                    if (after is null) throw new CommandProtocolException("correction_bounds_missing", $"Cannot measure corrected text: {edit.Handle}");
                    changes.Add(new { edit.Handle, before, after });
                }
                tx.Commit();
                NativeDrawing.Save(db, context.Config.OutputPath);
            }
            finally { HostApplicationServices.WorkingDatabase = previous; }
        }
        NativeDrawing.Report(context, "local-correction.json", new { inputSha256 = corrections.CandidateSha256,
            candidateSha256 = Hashing.Sha256File(context.Config.OutputPath), changes, requiresVisualReview = true });
        return CandidateInspection.Run(context.Derive(context.Config with { CandidateSha256 = Hashing.Sha256File(context.Config.OutputPath) }));
    }
}

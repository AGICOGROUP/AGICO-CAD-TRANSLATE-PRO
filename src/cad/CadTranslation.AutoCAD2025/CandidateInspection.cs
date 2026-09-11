using System.Text.Json;
using CadTranslation.Contracts;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

// Reopen a saved candidate without translating, composing, or saving it again.
internal static class CandidateInspection
{
    internal static T Read<T>(JobContext context, string name) => JsonSerializer.Deserialize<T>(
        File.ReadAllText(Path.Combine(context.Config.ArtifactDirectory, name)), JsonDefaults.Options)
        ?? throw new CommandProtocolException("missing_inspection_receipt", name);

    internal static void RequireCandidate(JobContext context, string path)
    {
        if (string.IsNullOrWhiteSpace(context.Config.CandidateSha256) || !File.Exists(path) ||
            Hashing.Sha256File(path) != context.Config.CandidateSha256)
            throw new CommandProtocolException("candidate_changed", "Saved candidate is missing or its hash changed.");
    }

    internal static int Run(JobContext context)
    {
        RequireCandidate(context, context.Config.OutputPath);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var manifest = NativeDrawing.ReadRows<ManifestRecord>(context.Config.ManifestPath);
            var translations = NativeDrawing.ReadRows<TranslationRecord>(context.Config.TranslationPath!);
            var validation = TranslationValidator.ValidateBatch(manifest, translations);
            if (!validation.IsValid)
            {
                var error = new CommandProtocolException("invalid_batch", "Repair the reported translations before inspecting the candidate.");
                error.Data["validationErrors"] = validation.Errors;
                throw error;
            }
            if (context.Config.OutputMode == "bilingual")
            {
                var receipt = Read<PairsReceipt>(context, "bilingual-pairs.json");
                var byId = manifest.ToDictionary(r => r.RecordId);
                var byHandle = manifest.ToDictionary(r => r.Handle, StringComparer.OrdinalIgnoreCase);
                var translated = translations.ToDictionary(r => r.RecordId);
                var paired = receipt.Pairs.Select(p => p.RecordId).ToHashSet();
                foreach (var row in manifest)
                    if (translated[row.RecordId].TranslatedText != row.FormatTemplate && !paired.Contains(row.RecordId))
                        throw new CommandProtocolException("missing_pair", $"Missing translation association: {row.RecordId}");
                foreach (var pair in receipt.Pairs)
                {
                    if (!byId.TryGetValue(pair.RecordId, out var row) || pair.SourceHandle != row.Handle)
                        throw new CommandProtocolException("invalid_pair", $"Invalid source association: {pair.RecordId}");
                    string expected = BilingualDrawingImporter.Plain(TranslationValidator.RestoreProtectedTokensForOutput(
                        translated[row.RecordId].TranslatedText, row.ProtectedTokens));
                    // An inline receipt can include source text. The native saved check still proves source retention.
                    bool retainedLabel = pair.Decision is "existing-neighbor" or "existing-inline" &&
                        byHandle.TryGetValue(pair.TargetHandle, out var existing) &&
                        BilingualDrawingImporter.Plain(existing.RawText) == pair.TargetText;
                    if (!retainedLabel && !BilingualLabelEquivalence.Matches(pair.TargetText, expected) &&
                        BilingualDrawingImporter.Normalize(pair.TargetText) != BilingualDrawingImporter.Normalize(expected))
                        throw new CommandProtocolException("stale_pair_translation", $"Candidate was built from different translation: {row.RecordId}");
                }
                var copies = File.Exists(Path.Combine(context.Config.ArtifactDirectory, "bilingual-table-layout.json"))
                    ? Read<TableReceipt>(context, "bilingual-table-layout.json").Copies : Array.Empty<BilingualTableCopy.CopyReceipt>();
                string limits = Path.Combine(context.Config.ArtifactDirectory, "bilingual-placement-limits.json");
                return BilingualDrawingImporter.VerifySaved(context, manifest, translated, receipt.Pairs, receipt.Unresolved,
                    copies, File.Exists(limits) ? Read<Dictionary<string, Rect2>>(context, "bilingual-placement-limits.json") : new(), new());
            }
            string sealPath = Path.Combine(context.Config.ArtifactDirectory, "replace-content-seal.json");
            if (!File.Exists(sealPath))
            {
                // Old per-entity candidates can still be proven from the source and approved translations.
                // Legacy composed outputs without a receipt fail here instead of trusting stale audit claims.
                DrawingVerifier.Verify(context);
                NativeDrawing.ExportCandidate(context);
                SealReplacement(context);
            }
            else
            {
                var seal = Read<ContentSeal>(context, "replace-content-seal.json");
                if (seal.SourceSha256 != context.Config.SourceSha256 || seal.TranslationSha256 != Hashing.Sha256File(context.Config.TranslationPath!))
                    throw new CommandProtocolException("stale_content_seal", "Saved content belongs to a different source or translation batch.");
                NativeDrawing.ExportCandidate(context);
                var current = CandidateRows(context);
                if (!ContentRows(seal.Rows).SequenceEqual(ContentRows(current)))
                    throw new CommandProtocolException("candidate_text_changed", "Saved candidate text, ownership, or style differs from the imported content.");
            }
            DrawingVerifier.VerifyStructure(context, "replace-structure.json");
            NativeDrawing.Report(context, "replace-native-check.json", new { status = "passed", outputMode = "replace",
                candidateSha256 = Hashing.Sha256File(context.Config.OutputPath), sourceSha256 = context.Config.SourceSha256 });
            return manifest.Length;
        }
        finally { NativeDrawing.Report(context, "inspection-timing.json", new { seconds = watch.Elapsed.TotalSeconds, operation = context.Config.Operation }); }
    }

    internal static ManifestRecord[] CandidateRows(JobContext context) => NativeDrawing.ReadRows<ManifestRecord>(
        Path.Combine(context.Config.ArtifactDirectory, context.Config.OutputMode + "-candidate.jsonl"));

    private static IEnumerable<string> ContentRows(IEnumerable<ManifestRecord> rows) => rows.Select(r =>
        JsonSerializer.Serialize(new { r.Handle, r.Slot, r.RawText, r.ObjectType, OwnerPath = NonTextStructureSignaturePolicy.StableOwnerPath(r.OwnerPath), r.Properties.Layer, r.Properties.TextStyle }))
        .OrderBy(r => r, StringComparer.Ordinal);

    internal static void SealReplacement(JobContext context) => NativeDrawing.Report(context, "replace-content-seal.json",
        new ContentSeal(context.Config.SourceSha256, Hashing.Sha256File(context.Config.TranslationPath!), CandidateRows(context)));

    internal sealed record PairsReceipt(string OutputMode, BilingualDrawingImporter.Pair[] Pairs, string[] Unresolved);
    internal sealed record TableReceipt(JsonElement Tables, BilingualTableCopy.CopyReceipt[] Copies);
    private sealed record ContentSeal(string SourceSha256, string TranslationSha256, ManifestRecord[] Rows);
}

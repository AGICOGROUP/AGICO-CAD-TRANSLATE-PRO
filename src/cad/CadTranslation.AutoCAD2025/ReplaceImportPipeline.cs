using Autodesk.AutoCAD.DatabaseServices;
using CadTranslation.Core;
using System.Text.Json;
using CadTranslation.Contracts;

namespace CadTranslation.AutoCAD2025;

internal static class ReplaceImportPipeline
{
    internal const string Version = "replace-v2";

    internal static int Run(JobContext context)
    {
        int count = ReplaceDrawingImporter.Run(context);
        DrawingVerifier.Verify(context);
        if (context.Config.TargetLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            // Legacy prose composition can prefer an existing English paragraph.
            // Chinese replacement keeps the verified per-entity output instead.
            NativeDrawing.Report(context, "logical-flow-report.json", new { replacedRecords = 0, composedObjects = 0,
                rows = Array.Empty<object>(), reason = "verified per-entity Chinese replacement" });
            DrawingVerifier.VerifyStructure(context, "replace-structure.json");
            NativeDrawing.ExportCandidate(context);
            CandidateInspection.SealReplacement(context);
            NativeDrawing.Report(context, "replace-native-check.json", new { status = "passed", outputMode = "replace",
                candidateSha256 = Hashing.Sha256File(context.Config.OutputPath), sourceSha256 = context.Config.SourceSha256 });
            return count;
        }
        string output = context.Config.OutputPath;
        string extension = Path.GetExtension(output);
        string input = Path.Combine(context.Config.ArtifactDirectory, "replace-before-compose" + extension);
        File.Move(output, input);
        var compose = context.Derive(context.Config with { WorkingPath = input, SourcePath = input,
            SourceSha256 = Hashing.Sha256File(input) });
        LogicalFlowPrototype.Run(compose);
        NativeDrawing.ExportCandidate(context);
        CandidateInspection.SealReplacement(context);
        DrawingVerifier.VerifyStructure(context, "replace-structure.json");
        NativeDrawing.Report(context, "replace-native-check.json", new { status = "passed", outputMode = "replace",
            candidateSha256 = Hashing.Sha256File(output), sourceSha256 = context.Config.SourceSha256 });
        return count;
    }

    internal static LayoutOptimizationResult Optimize(
        Database database,
        Transaction transaction,
        IReadOnlyList<LayoutTargetSnapshot> targets,
        CadLayoutBaseline baseline) =>
        LayoutOptimizer.Optimize(database, transaction, targets, baseline);
}

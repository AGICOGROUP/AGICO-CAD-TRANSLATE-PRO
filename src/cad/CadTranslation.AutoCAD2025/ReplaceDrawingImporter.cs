using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using CadTranslation.AutoCAD2025.Adapters;
using CadTranslation.Contracts;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

internal static class ReplaceDrawingImporter
{
    private static readonly ITextAdapter[] Adapters = [new DbTextAdapter(), new MTextAdapter(), new AttributeAdapter(), new DimensionAdapter()];

    internal static int Run(JobContext context)
    {
        const string pipelineVersion = "replace-v2";
        const string artifactPrefix = "replace";
        context.VerifySourceAndWorkingHashes();
        if (string.IsNullOrWhiteSpace(context.Config.TranslationPath))
            throw new CommandProtocolException("missing_translation", "translationPath is required for import.");
        RejectUnsafeOutput(context.Config);

        ManifestRecord[] manifest = ReadJsonLines<ManifestRecord>(context.Config.ManifestPath, "manifest");
        TranslationRecord[] translations = ReadJsonLines<TranslationRecord>(context.Config.TranslationPath, "translation");
        BatchValidationResult validation = TranslationValidator.ValidateBatch(manifest, translations);
        if (!validation.IsValid)
            throw new CommandProtocolException("invalid_translation_batch", string.Join("; ", validation.Errors.Select(error => error.Code)));

        string temporaryOutput = CreateSiblingTemporaryPath(context.Config.OutputPath);
        ResolvedWrite[] resolved;
        ResolvedWrite[] changed;
        LayoutOptimizationResult layoutResult;
        CadLayoutBaseline layoutBaseline;
        LayoutAuditReport layoutAudit;
        try
        {
            using (var sideDatabase = new Database(false, true))
            {
                ReadWorkingDrawing(sideDatabase, context.Config.WorkingPath, context.Config.ArtifactDirectory);
                Database originalWorkingDatabase = HostApplicationServices.WorkingDatabase;
                try
                {
                    HostApplicationServices.WorkingDatabase = sideDatabase;
                    var translationsById = translations.ToDictionary(record => record.RecordId, StringComparer.Ordinal);
                    resolved = ResolveAll(sideDatabase, manifest, translationsById, context.Config.SourceSha256);
                    using (Transaction transaction = sideDatabase.TransactionManager.StartTransaction())
                    {
                        var suppressed = new HashSet<string>(StringComparer.Ordinal);
                        changed = resolved.Where(write => ImportWriteDecision.NeedsWrite(write.CurrentRawText, write.RestoredText)).ToArray();
                        LayoutWriteInput[] layoutInputs = resolved
                            .Where(write => !suppressed.Contains(write.Manifest.RecordId))
                            .Select(write => new LayoutWriteInput(
                                write.ObjectId,
                                write.Manifest,
                                write.RestoredText,
                                ImportWriteDecision.NeedsLayout(
                                    write.Manifest.PlainText,
                                    translationsById[write.Manifest.RecordId].TranslatedText)))
                            .ToArray();
                        layoutBaseline = DrawingTopologyCapture.Capture(sideDatabase, transaction, layoutInputs);
                        LayoutTargetSnapshot[] layoutTargets = LayoutOptimizer.Capture(transaction, layoutInputs);
                        foreach (ResolvedWrite write in changed.Where(write => !suppressed.Contains(write.Manifest.RecordId)))
                        {
                            DBObject value = transaction.GetObject(write.ObjectId, OpenMode.ForWrite, false);
                            write.Adapter.Write(value, write.Manifest.Slot, write.RestoredText);
                        }
                        layoutResult = ReplaceImportPipeline.Optimize(sideDatabase, transaction, layoutTargets, layoutBaseline);
                        transaction.Commit();
                    }
                    SaveTemporaryOutput(sideDatabase, temporaryOutput, Path.GetExtension(context.Config.WorkingPath));
                }
                finally
                {
                    HostApplicationServices.WorkingDatabase = originalWorkingDatabase;
                }
            }

            layoutAudit = AuditTemporaryOutput(
                temporaryOutput,
                Path.GetExtension(context.Config.WorkingPath),
                layoutBaseline,
                layoutResult,
                passIndex: 1);
            AtomicFile.WriteUtf8(Path.Combine(context.Config.ArtifactDirectory, $"{artifactPrefix}-layout-adjustments.json"),
                JsonSerializer.Serialize(layoutResult, JsonDefaults.Options));
            AtomicFile.WriteUtf8(Path.Combine(context.Config.ArtifactDirectory, $"{artifactPrefix}-layout-audit.json"),
                JsonSerializer.Serialize(layoutAudit, JsonDefaults.Options));
            if (layoutAudit.ManualReview.Count > 0)
            {
                File.Copy(
                    temporaryOutput,
                    Path.Combine(context.Config.ArtifactDirectory, "review-candidate.dwg"),
                    overwrite: true);
            }
            ReplaceImportGate.EnsurePassed(layoutResult, layoutAudit);
            File.Move(temporaryOutput, context.Config.OutputPath, overwrite: true);
            AtomicFile.WriteUtf8(Path.Combine(context.Config.ArtifactDirectory, "import-summary.json"), JsonSerializer.Serialize(new
            {
                processedRecords = resolved.Length,
                outputMode = context.Config.OutputMode,
                pipelineVersion,
                changedRecordCount = changed.Length,
                changedRecordIds = changed.Select(write => write.Manifest.RecordId).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                manualReviewRecordIds = translations.Where(record => record.ReviewStatus == "manual-review").Select(record => record.RecordId).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                layoutPolicy = "wrap-then-compress-width-then-shrink-height-then-text-only-reflow",
                layout = new
                {
                    layoutResult.Wrapped,
                    layoutResult.WidthCompressed,
                    layoutResult.HeightReduced,
                    layoutResult.Reflowed,
                    layoutResult.ManualReview,
                    layoutResult.RemainingOverflow,
                    uncovered = layoutResult.UncoveredRecordIds.Count
                },
                topology = new
                {
                    definitions = layoutBaseline.Definitions.Count,
                    blockInstances = layoutBaseline.BlockInstances.Count,
                    regions = layoutBaseline.Definitions.Sum(definition => definition.Regions.Count),
                    tableCells = layoutBaseline.Definitions.Sum(definition => definition.Regions.Count(region => region.Kind == LayoutRegionKind.TableCell)),
                    noteColumns = layoutBaseline.Definitions.Sum(definition => definition.Regions.Count(region => region.Kind == LayoutRegionKind.NoteColumn)),
                    assignedTexts = layoutBaseline.Definitions.Sum(definition => definition.Texts.Count(text => text.Region is not null))
                },
                layoutAudit = new
                {
                    layoutAudit.PassIndex,
                    layoutAudit.CandidateReopened,
                    layoutAudit.ExpectedBlockInstances,
                    layoutAudit.AuditedBlockInstances,
                    layoutAudit.RiskCounts,
                    unresolvedHigh = layoutAudit.ManualReview.Count
                }
            }, JsonDefaults.Options));
            context.VerifySourceAndWorkingHashes();
            return resolved.Length;
        }
        finally
        {
            if (File.Exists(temporaryOutput)) File.Delete(temporaryOutput);
        }
    }

    private static ResolvedWrite[] ResolveAll(Database database, IReadOnlyList<ManifestRecord> manifest, IReadOnlyDictionary<string, TranslationRecord> translations, string sourceHash)
    {
        var resolved = new List<ResolvedWrite>(manifest.Count);
        using Transaction transaction = database.TransactionManager.StartTransaction();
        foreach (ManifestRecord record in manifest)
        {
            ObjectId objectId = ResolveObjectId(database, record);
            DBObject value = transaction.GetObject(objectId, OpenMode.ForRead, false);
            if (!ImportTargetContract.HasExactObjectType(record.ObjectType, value.GetRXClass().Name))
                throw new CommandProtocolException("object_type_mismatch", "Manifest objectType does not exactly match the resolved AutoCAD RXClass.");
            ITextAdapter adapter = Adapters.FirstOrDefault(candidate => candidate.CanWriteSlot(value, record.Slot))
                ?? throw new CommandProtocolException("unsupported_slot", "Manifest slot is not supported by the MVP importer.");
            var adapterContext = new AdapterContext(database, transaction, sourceHash, record.OwnerPath);
            TextSlot current = adapter.Read(value, adapterContext).SingleOrDefault(slot => slot.Slot == record.Slot)
                ?? throw new CommandProtocolException("stale_drawing_text", "Target text slot is no longer readable.");
            VerifyInputSnapshot(record, current);
            TranslationRecord translation = translations[record.RecordId];
            string restoredText = TranslationValidator.RestoreProtectedTokensForOutput(translation.TranslatedText, record.ProtectedTokens);
            resolved.Add(new ResolvedWrite(objectId, adapter, record, current.RawText, restoredText));
        }
        transaction.Commit();
        return resolved.ToArray();
    }

    private static ObjectId ResolveObjectId(Database database, ManifestRecord record)
    {
        if (!long.TryParse(record.Handle, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long handleValue))
            throw new CommandProtocolException("invalid_handle", "Manifest handle is not valid hexadecimal.");
        try
        {
            ObjectId id = database.GetObjectId(false, new Handle(handleValue), 0);
            if (id.IsNull) throw new CommandProtocolException("missing_handle", "Manifest handle no longer exists.");
            return id;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception exception)
        {
            throw new CommandProtocolException("missing_handle", "Manifest handle no longer exists.", exception);
        }
    }

    private static void VerifyInputSnapshot(ManifestRecord record, TextSlot current)
    {
        ParsedText parsed = ProtectedText.Parse(current.RawText);
        string actual = Hashing.Sha256Text($"{record.RecordId}|{current.RawText}|{parsed.FormatTemplate}|{JsonSerializer.Serialize(current.Properties, JsonDefaults.Options)}");
        if (!string.Equals(record.RawText, current.RawText, StringComparison.Ordinal) || !string.Equals(record.InputHash, actual, StringComparison.Ordinal))
            throw new CommandProtocolException("stale_drawing_text", $"Handle {record.Handle}: drawing text or protected properties changed after export. Expected properties: {JsonSerializer.Serialize(record.Properties, JsonDefaults.Options)}; actual properties: {JsonSerializer.Serialize(current.Properties, JsonDefaults.Options)}; raw text equal: {string.Equals(record.RawText, current.RawText, StringComparison.Ordinal)}.");
    }

    private static T[] ReadJsonLines<T>(string path, string name)
    {
        if (!File.Exists(path)) throw new CommandProtocolException($"missing_{name}", $"{name} file does not exist.");
        try
        {
            return File.ReadLines(path)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonSerializer.Deserialize<T>(line, JsonDefaults.Options) ?? throw new JsonException("Record cannot be null."))
                .ToArray();
        }
        catch (JsonException exception)
        {
            throw new CommandProtocolException($"invalid_{name}", $"{name} must be valid JSONL matching schema 1.0.", exception);
        }
    }

    private static void RejectUnsafeOutput(JobConfig config)
    {
        string output = Path.GetFullPath(config.OutputPath);
        if (string.Equals(output, Path.GetFullPath(config.SourcePath), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(output, Path.GetFullPath(config.WorkingPath), StringComparison.OrdinalIgnoreCase))
            throw new CommandProtocolException("unsafe_output_path", "Import output cannot overwrite source or working drawing.");
        string extension = Path.GetExtension(config.WorkingPath);
        if (extension is not (".dwg" or ".dxf") || !string.Equals(extension, Path.GetExtension(output), StringComparison.OrdinalIgnoreCase))
            throw new CommandProtocolException("unsupported_output_format", "Candidate output must preserve the working drawing format.");
    }

    private static string CreateSiblingTemporaryPath(string outputPath) => Path.Combine(
        Path.GetDirectoryName(outputPath) ?? throw new CommandProtocolException("invalid_output_path", "outputPath requires a parent directory."),
        $".{Path.GetFileNameWithoutExtension(outputPath)}.{Guid.NewGuid():N}.tmp{Path.GetExtension(outputPath)}");

    private static void ReadWorkingDrawing(Database database, string path, string artifactDirectory)
    {
        string extension = Path.GetExtension(path);
        if (extension.Equals(".dwg", StringComparison.OrdinalIgnoreCase))
        {
            database.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, string.Empty);
            database.CloseInput(true);
            return;
        }
        if (extension.Equals(".dxf", StringComparison.OrdinalIgnoreCase))
        {
            string logPath = Path.Combine(artifactDirectory, $".dxf-import.{Guid.NewGuid():N}.log");
            try
            {
                database.DxfIn(path, logPath);
            }
            finally
            {
                if (File.Exists(logPath)) File.Delete(logPath);
            }
            return;
        }
        throw new CommandProtocolException("unsupported_input_format", "Working drawing must be DWG or DXF.");
    }

    private static void SaveTemporaryOutput(Database database, string temporaryOutput, string extension)
    {
        if (extension.Equals(".dwg", StringComparison.OrdinalIgnoreCase))
            database.SaveAs(temporaryOutput, true, database.OriginalFileVersion, database.SecurityParameters);
        else if (extension.Equals(".dxf", StringComparison.OrdinalIgnoreCase))
            database.DxfOut(temporaryOutput, 16, database.OriginalFileVersion);
        else
            throw new CommandProtocolException("unsupported_output_format", "Candidate output must be DWG or DXF.");
    }

    private static LayoutAuditReport AuditTemporaryOutput(
        string temporaryOutput,
        string extension,
        CadLayoutBaseline baseline,
        LayoutOptimizationResult optimization,
        int passIndex)
    {
        using var reopened = new Database(false, true);
        if (extension.Equals(".dwg", StringComparison.OrdinalIgnoreCase)) reopened.ReadDwgFile(temporaryOutput, FileOpenMode.OpenForReadAndAllShare, true, string.Empty);
        else if (extension.Equals(".dxf", StringComparison.OrdinalIgnoreCase)) reopened.DxfIn(temporaryOutput, null);
        else throw new CommandProtocolException("unsupported_output_format", "Candidate output must be DWG or DXF.");
        reopened.CloseInput(true);
        return LayoutAuditor.Audit(reopened, baseline, optimization, passIndex);
    }

    private sealed record ResolvedWrite(ObjectId ObjectId, ITextAdapter Adapter, ManifestRecord Manifest, string CurrentRawText, string RestoredText);
}

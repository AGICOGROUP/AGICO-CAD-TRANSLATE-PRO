using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using CadTranslation.AutoCAD2025.Adapters;
using CadTranslation.Contracts;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

/// <summary>Reads sealed drawings through side databases and rejects unsafe candidate changes.</summary>
internal static class DrawingVerifier
{
    private const string SchemaVersion = "1.0";
    private static readonly ITextAdapter[] Adapters = [new DbTextAdapter(), new MTextAdapter(), new AttributeAdapter(), new DimensionAdapter()];

    internal static int Verify(JobContext context)
    {
        var componentSignatures = new SortedDictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        string sourceHash = context.Config.SourceSha256;
        string workingHash = string.Empty;
        string candidateHash = string.Empty;
        int processed = 0;
        try
        {
            context.VerifySourceAndWorkingHashes();
            if (!File.Exists(context.Config.OutputPath))
                throw new CommandProtocolException("missing_candidate_file", "Candidate output drawing does not exist.");

            sourceHash = Hashing.Sha256File(context.Config.SourcePath);
            workingHash = Hashing.Sha256File(context.Config.WorkingPath);
            candidateHash = Hashing.Sha256File(context.Config.OutputPath);
            DrawingStructureSnapshot source = CaptureSnapshot(context.Config.SourcePath, "source", context.Config.ArtifactDirectory);
            DrawingStructureSnapshot working = CaptureSnapshot(context.Config.WorkingPath, "working", context.Config.ArtifactDirectory);
            DrawingStructureSnapshot candidate = CaptureSnapshot(context.Config.OutputPath, "candidate", context.Config.ArtifactDirectory);
            componentSignatures["source"] = source.ComponentSignatures;
            componentSignatures["working"] = working.ComponentSignatures;
            componentSignatures["candidate"] = candidate.ComponentSignatures;
            var errors = new List<CommandError>();
            AddStructureError(source, working, "working_structure_mismatch", errors);
            AddStructureError(source, candidate, "candidate_structure_mismatch", errors, ["tables", "nonText"]);

            ManifestRecord[] manifest = ReadJsonLines<ManifestRecord>(context.Config.ManifestPath, "manifest");
            if (string.IsNullOrWhiteSpace(context.Config.TranslationPath))
                throw new CommandProtocolException("missing_translation", "translationPath is required for verification.");
            TranslationRecord[] translations = ReadJsonLines<TranslationRecord>(context.Config.TranslationPath, "translation");
            LayoutAuditReport layoutAudit = ReadJsonFile<LayoutAuditReport>(
                Path.Combine(context.Config.ArtifactDirectory, context.Config.OutputMode + "-layout-audit.json"),
                "layout_audit");
            Dictionary<string, CandidateIdentityOverride> identityOverrides = layoutAudit.Texts
                .Where(text => !string.Equals(text.OldHandle, text.NewHandle, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(
                    text => text.RecordId,
                    text => new CandidateIdentityOverride(text.NewHandle, "AcDbMText", "contents"),
                    StringComparer.Ordinal);
            HashSet<string> widthCompressed = layoutAudit.Texts
                .Where(text => text.Actions.Contains("compress-width", StringComparer.Ordinal))
                .Select(text => text.RecordId)
                .ToHashSet(StringComparer.Ordinal);
            CandidateTextRecord[] candidateRecords = ReadCandidateRecords(
                context.Config.OutputPath,
                context.Config.ArtifactDirectory,
                manifest,
                sourceHash,
                identityOverrides,
                widthCompressed);
            VerificationResult content = CandidateContentVerifier.Verify(
                manifest,
                translations,
                candidateRecords,
                identityOverrides);
            processed = candidateRecords.Length;
            errors.AddRange(content.Errors);
            if (errors.Count > 0)
                throw new VerificationFailureException("candidate_verification_failed", "Candidate did not pass structural and content verification.", errors);

            WriteReport(context, "passed", processed, Array.Empty<CommandError>(), sourceHash, workingHash, candidateHash, componentSignatures);
            return processed;
        }
        catch (VerificationFailureException exception)
        {
            WriteReport(context, "failed", processed, exception.Errors, sourceHash, string.IsNullOrEmpty(workingHash) ? TryHash(context.Config.WorkingPath) : workingHash, candidateHash, componentSignatures);
            throw new CommandProtocolException(exception.Code, exception.Message, exception);
        }
        catch (CommandProtocolException exception)
        {
            WriteReport(context, "failed", processed, new[] { new CommandError(exception.Code, exception.Message, null, null) }, sourceHash,
                string.IsNullOrEmpty(workingHash) ? TryHash(context.Config.WorkingPath) : workingHash, candidateHash, componentSignatures);
            throw;
        }
        catch (System.Exception exception)
        {
            WriteReport(context, "failed", processed, new[] { new CommandError("verification_failed", exception.Message, null, null) }, sourceHash,
                string.IsNullOrEmpty(workingHash) ? TryHash(context.Config.WorkingPath) : workingHash, candidateHash, componentSignatures);
            throw new CommandProtocolException("verification_failed", "Candidate verification did not complete.", exception);
        }
    }

    private static CandidateTextRecord[] ReadCandidateRecords(
        string candidatePath,
        string artifactDirectory,
        IReadOnlyList<ManifestRecord> manifest,
        string sourceHash,
        IReadOnlyDictionary<string, CandidateIdentityOverride> identityOverrides,
        IReadOnlySet<string> widthCompressed)
    {
        using var database = new Database(false, true);
        ReadDrawing(database, candidatePath, artifactDirectory);
        using Transaction transaction = database.TransactionManager.StartTransaction();
        var result = new List<CandidateTextRecord>(manifest.Count);
        foreach (ManifestRecord record in manifest)
        {
            CandidateIdentityOverride? identity = identityOverrides.GetValueOrDefault(record.RecordId);
            string expectedHandle = identity?.Handle ?? record.Handle;
            string expectedSlot = identity?.Slot ?? record.Slot;
            ObjectId objectId = ResolveObjectId(database, expectedHandle);
            DBObject value = transaction.GetObject(objectId, OpenMode.ForRead, false);
            string actualType = value.GetRXClass().Name;
            ITextAdapter? adapter = Adapters.FirstOrDefault(candidate => candidate.CanWriteSlot(value, expectedSlot));
            if (adapter is null)
                throw new CommandProtocolException("candidate_unsupported_slot", "Candidate slot is no longer supported by the MVP adapter.");
            TextSlot slot = adapter.Read(value, new AdapterContext(database, transaction, sourceHash, record.OwnerPath))
                .SingleOrDefault(candidate => string.Equals(candidate.Slot, expectedSlot, StringComparison.Ordinal))
                ?? throw new CommandProtocolException("candidate_stale_text", "Candidate text slot is no longer readable.");
            string actualText = widthCompressed.Contains(record.RecordId)
                ? LayoutTextNormalization.RemoveGeneratedWidthWrapper(slot.RawText)
                : slot.RawText;
            result.Add(new CandidateTextRecord(record.RecordId, value.Handle.ToString(), actualType, slot.Slot, actualText));
        }
        transaction.Commit();
        return result.ToArray();
    }

    private static DrawingStructureSnapshot CaptureSnapshot(string path, string label, string artifactDirectory)
    {
        using var database = new Database(false, true);
        ReadDrawing(database, path, artifactDirectory);
        using Transaction transaction = database.TransactionManager.StartTransaction();
        var tableRows = new List<string> { $"version|{database.OriginalFileVersion}" };
        var nonTextRows = new List<string>();
        var textEntities = new List<TextStructureSignature>();
        AddSymbolTable(tableRows, transaction, database.LayerTableId, "layers");
        AddSymbolTable(tableRows, transaction, database.LinetypeTableId, "linetypes");
        AddSymbolTable(tableRows, transaction, database.TextStyleTableId, "textStyles");
        AddSymbolTable(tableRows, transaction, database.DimStyleTableId, "dimensionStyles");
        AddBlocks(tableRows, transaction, database.BlockTableId);
        AddLayouts(tableRows, transaction, database.LayoutDictionaryId);
        AddEntityStructure(nonTextRows, textEntities, database, transaction);
        transaction.Commit();
        var rows = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["tables"] = tableRows.OrderBy(row => row, StringComparer.Ordinal).ToArray(),
            ["nonText"] = nonTextRows.OrderBy(row => row, StringComparer.Ordinal).ToArray(),
            ["textStructure"] = DrawingStructureSignature.TextRows(textEntities)
        };
        return new DrawingStructureSnapshot(label,
            rows.ToDictionary(pair => pair.Key, pair => DrawingStructureSignature.ComputeRows(pair.Value), StringComparer.Ordinal), rows);
    }

    private static void AddStructureError(
        DrawingStructureSnapshot expected,
        DrawingStructureSnapshot actual,
        string code,
        ICollection<CommandError> errors,
        IReadOnlyList<string>? requiredComponents = null)
    {
        IEnumerable<string> components = requiredComponents ?? expected.ComponentSignatures.Keys;
        foreach (string component in components.OrderBy(key => key, StringComparer.Ordinal))
        {
            if (string.Equals(expected.ComponentSignatures[component], actual.ComponentSignatures[component], StringComparison.Ordinal)) continue;
            errors.Add(new CommandError(code,
                $"{actual.Label} drawing structure differs from {expected.Label}; component={component}; firstDifference={FirstDifference(expected.Rows[component], actual.Rows[component])}", null, null));
            return;
        }
    }

    private static string FirstDifference(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        int count = Math.Min(expected.Count, actual.Count);
        for (int index = 0; index < count; index++)
        {
            if (!string.Equals(expected[index], actual[index], StringComparison.Ordinal))
                return $"expected={expected[index]}; actual={actual[index]}";
        }
        return expected.Count == actual.Count ? "none" : expected.Count > actual.Count
            ? $"expected={expected[count]}; actual=<missing>"
            : $"expected=<missing>; actual={actual[count]}";
    }

    private static void AddSymbolTable(ICollection<string> rows, Transaction transaction, ObjectId tableId, string tableName)
    {
        var table = (SymbolTable)transaction.GetObject(tableId, OpenMode.ForRead);
        var entries = new List<string>();
        foreach (ObjectId id in table)
        {
            var record = (SymbolTableRecord)transaction.GetObject(id, OpenMode.ForRead);
            entries.Add($"{record.Name}|{record.Handle}");
        }
        rows.Add($"table|{tableName}|count={entries.Count}");
        foreach (string entry in entries.OrderBy(entry => entry, StringComparer.Ordinal)) rows.Add($"table|{tableName}|{entry}");
    }

    private static void AddBlocks(ICollection<string> rows, Transaction transaction, ObjectId tableId)
    {
        var table = (BlockTable)transaction.GetObject(tableId, OpenMode.ForRead);
        var entries = new List<string>();
        foreach (ObjectId id in table)
        {
            var record = (BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead);
            entries.Add($"{record.Name}|{record.Handle}|xref={record.IsFromExternalReference}|overlay={record.IsFromOverlayReference}");
        }
        rows.Add($"table|blocks|count={entries.Count}");
        rows.Add($"table|xrefs|count={entries.Count(entry => entry.Contains("xref=True", StringComparison.Ordinal))}");
        foreach (string entry in entries.OrderBy(entry => entry, StringComparer.Ordinal)) rows.Add($"table|blocks|{entry}");
    }

    private static void AddLayouts(ICollection<string> rows, Transaction transaction, ObjectId dictionaryId)
    {
        var dictionary = (DBDictionary)transaction.GetObject(dictionaryId, OpenMode.ForRead);
        var entries = new List<string>();
        foreach (DBDictionaryEntry entry in dictionary)
        {
            var layout = (Layout)transaction.GetObject(entry.Value, OpenMode.ForRead);
            entries.Add($"{layout.LayoutName}|{layout.Handle}|{layout.BlockTableRecordId.Handle}");
        }
        rows.Add($"table|layouts|count={entries.Count}");
        foreach (string entry in entries.OrderBy(entry => entry, StringComparer.Ordinal)) rows.Add($"table|layouts|{entry}");
    }

    private static void AddEntityStructure(ICollection<string> rows, ICollection<TextStructureSignature> textEntities, Database database, Transaction transaction)
    {
        foreach (WalkItem item in EntityWalker.Walk(database, transaction))
        {
            if (item.Value is not Entity entity) continue;
            TextStructureSignature? textSignature = TextSignature(entity, item.OwnerPath);
            if (textSignature is not null)
            {
                textEntities.Add(textSignature);
                continue;
            }
            string stablePlacement = entity is BlockReference reference
                ? BlockReferencePlacement(reference)
                : string.Empty;
            rows.Add(string.Join("|", "entity", entity.Handle, entity.GetRXClass().Name, item.OwnerPath, entity.Layer,
                entity.ColorIndex.ToString(CultureInfo.InvariantCulture), entity.LineWeight.ToString(),
                NonTextStructureSignaturePolicy.GeometryToken(
                    entity.GetRXClass().Name,
                    stablePlacement,
                    Extents(entity))));
        }
    }

    private static string BlockReferencePlacement(BlockReference reference) => string.Join("|",
        $"definition={reference.BlockTableRecord.Handle}",
        $"position={Point(reference.Position)}",
        $"rotation={Number(reference.Rotation)}",
        $"scale={Number(reference.ScaleFactors.X)},{Number(reference.ScaleFactors.Y)},{Number(reference.ScaleFactors.Z)}",
        $"normal={Point(reference.Normal)}");

    private static TextStructureSignature? TextSignature(Entity entity, string ownerPath)
    {
        return entity switch
        {
            AttributeDefinition attributeDefinition => DbTextSignature(attributeDefinition, ownerPath, $"tag={attributeDefinition.Tag}"),
            AttributeReference attributeReference => DbTextSignature(attributeReference, ownerPath, $"tag={attributeReference.Tag}"),
            DBText text => DbTextSignature(text, ownerPath, string.Empty),
            MText text => new TextStructureSignature(text.Handle.ToString(), text.GetRXClass().Name, ownerPath, text.Layer,
                text.ColorIndex.ToString(CultureInfo.InvariantCulture), text.LineWeight.ToString(), string.Join("|",
                    $"location={Point(text.Location)}", $"width={Number(text.Width)}", $"attachment={text.Attachment}",
                    $"direction={Point(text.Direction)}", $"rotation={Number(text.Rotation)}", $"lineSpacing={text.LineSpacingStyle}:{Number(text.LineSpacingFactor)}",
                    $"style={text.TextStyleId.Handle}")),
            Dimension dimension => new TextStructureSignature(dimension.Handle.ToString(), dimension.GetRXClass().Name, ownerPath, dimension.Layer,
                dimension.ColorIndex.ToString(CultureInfo.InvariantCulture), dimension.LineWeight.ToString(), string.Join("|",
                    $"style={dimension.DimensionStyle.Handle}", $"normal={Point(dimension.Normal)}", $"textPosition={Point(dimension.TextPosition)}",
                    $"textRotation={Number(dimension.TextRotation)}", DimensionDefinitionPoints(dimension))),
            _ => null
        };
    }

    private static TextStructureSignature DbTextSignature(DBText text, string ownerPath, string typeSpecific) => new(
        text.Handle.ToString(), text.GetRXClass().Name, ownerPath, text.Layer, text.ColorIndex.ToString(CultureInfo.InvariantCulture),
        text.LineWeight.ToString(), string.Join("|", $"position={Point(text.Position)}", $"alignment={Point(text.AlignmentPoint)}", $"normal={Point(text.Normal)}", $"rotation={Number(text.Rotation)}",
            $"height={Number(text.Height)}", $"width={Number(text.WidthFactor)}", $"oblique={Number(text.Oblique)}", $"style={text.TextStyleName}", typeSpecific));

    private static string DimensionDefinitionPoints(Dimension dimension) => dimension switch
    {
        RotatedDimension value => $"defpoints={Point(value.XLine1Point)};{Point(value.XLine2Point)};{Point(value.DimLinePoint)}|rotation={Number(value.Rotation)}",
        AlignedDimension value => $"defpoints={Point(value.XLine1Point)};{Point(value.XLine2Point)};{Point(value.DimLinePoint)}",
        RadialDimension value => $"defpoints={Point(value.Center)};{Point(value.ChordPoint)}|leaderLength={Number(value.LeaderLength)}",
        DiametricDimension value => $"defpoints={Point(value.ChordPoint)};{Point(value.FarChordPoint)}|leaderLength={Number(value.LeaderLength)}",
        OrdinateDimension value => $"defpoints={Point(value.Origin)};{Point(value.DefiningPoint)};{Point(value.LeaderEndPoint)}",
        ArcDimension value => $"defpoints={Point(value.XLine1Point)};{Point(value.XLine2Point)};{Point(value.ArcPoint)};{Point(value.CenterPoint)}",
        LineAngularDimension2 value => $"defpoints={Point(value.XLine1Start)};{Point(value.XLine1End)};{Point(value.XLine2Start)};{Point(value.XLine2End)};{Point(value.ArcPoint)}",
        Point3AngularDimension value => $"defpoints={Point(value.CenterPoint)};{Point(value.XLine1Point)};{Point(value.XLine2Point)};{Point(value.ArcPoint)}",
        _ => "defpoints=unsupported"
    };

    private static string Point(Autodesk.AutoCAD.Geometry.Point3d value) => string.Join(",", Number(value.X), Number(value.Y), Number(value.Z));
    private static string Point(Autodesk.AutoCAD.Geometry.Vector3d value) => string.Join(",", Number(value.X), Number(value.Y), Number(value.Z));
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Extents(Entity entity)
    {
        try
        {
            Extents3d extents = entity.GeometricExtents;
            return string.Join(",", extents.MinPoint.X.ToString("R", CultureInfo.InvariantCulture), extents.MinPoint.Y.ToString("R", CultureInfo.InvariantCulture),
                extents.MinPoint.Z.ToString("R", CultureInfo.InvariantCulture), extents.MaxPoint.X.ToString("R", CultureInfo.InvariantCulture),
                extents.MaxPoint.Y.ToString("R", CultureInfo.InvariantCulture), extents.MaxPoint.Z.ToString("R", CultureInfo.InvariantCulture));
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return "unavailable";
        }
    }

    private static ObjectId ResolveObjectId(Database database, string handle)
    {
        if (!long.TryParse(handle, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long handleValue))
            throw new CommandProtocolException("candidate_invalid_handle", "Manifest handle is not valid hexadecimal.");
        try
        {
            ObjectId id = database.GetObjectId(false, new Handle(handleValue), 0);
            if (id.IsNull) throw new CommandProtocolException("candidate_missing_handle", "Candidate handle no longer exists.");
            return id;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception exception)
        {
            throw new CommandProtocolException("candidate_missing_handle", "Candidate handle no longer exists.", exception);
        }
    }

    private static void ReadDrawing(Database database, string path, string artifactDirectory)
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
            string logPath = Path.Combine(artifactDirectory, $".verify-dxf.{Guid.NewGuid():N}.log");
            try { database.DxfIn(path, logPath); }
            finally { if (File.Exists(logPath)) File.Delete(logPath); }
            return;
        }
        throw new CommandProtocolException("unsupported_candidate_format", "Verification supports DWG and DXF only.");
    }

    private static T[] ReadJsonLines<T>(string path, string name)
    {
        if (!File.Exists(path)) throw new CommandProtocolException($"missing_{name}", $"{name} file does not exist.");
        try
        {
            return File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonSerializer.Deserialize<T>(line, JsonDefaults.Options) ?? throw new JsonException("Record cannot be null.")).ToArray();
        }
        catch (JsonException exception)
        {
            throw new CommandProtocolException($"invalid_{name}", $"{name} must be valid JSONL matching schema 1.0.", exception);
        }
    }

    private static T ReadJsonFile<T>(string path, string name)
    {
        if (!File.Exists(path))
            throw new CommandProtocolException($"missing_{name}", $"{name} file does not exist.");
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonDefaults.Options)
                   ?? throw new JsonException("Document cannot be null.");
        }
        catch (JsonException exception)
        {
            throw new CommandProtocolException($"invalid_{name}", $"{name} must be valid JSON matching schema 1.0.", exception);
        }
    }

    private static void WriteReport(JobContext context, string status, int processed, IReadOnlyList<CommandError> errors, string sourceHash, string workingHash, string candidateHash, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> componentSignatures)
    {
        var report = new VerificationReport(SchemaVersion, context.Config.JobId, status, processed,
            errors.Select(error => error.Code).OrderBy(code => code, StringComparer.Ordinal).ToArray(), errors,
            sourceHash, workingHash, candidateHash, componentSignatures, DateTimeOffset.UtcNow);
        AtomicFile.WriteUtf8(Path.Combine(context.Config.ArtifactDirectory, "verification.json"), JsonSerializer.Serialize(report, JsonDefaults.Options));
    }

    private static string TryHash(string path) => File.Exists(path) ? Hashing.Sha256File(path) : string.Empty;

    private sealed record DrawingStructureSnapshot(string Label, IReadOnlyDictionary<string, string> ComponentSignatures,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Rows);
    private sealed class VerificationFailureException(string code, string message, IReadOnlyList<CommandError> errors) : Exception(message)
    {
        internal string Code { get; } = code;
        internal IReadOnlyList<CommandError> Errors { get; } = errors;
    }
    private sealed record VerificationReport(string SchemaVersion, string JobId, string Status, int ProcessedRecords,
        IReadOnlyList<string> ErrorCodes, IReadOnlyList<CommandError> Errors, string SourceSha256, string WorkingSha256,
        string CandidateSha256, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ComponentSignatures, DateTimeOffset FinishedAtUtc);
}

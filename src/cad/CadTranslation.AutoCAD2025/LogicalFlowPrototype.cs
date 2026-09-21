using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadTranslation.Contracts;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

internal static partial class LogicalFlowPrototype
{
    private const double MinimumBodyScale = 0.50;

    internal static int Run(JobContext context)
    {
        if (context.Config.TranslationPath is null)
        {
            throw new CommandProtocolException("missing_translation", "compose requires translationPath.");
        }

        string auditPath = Path.Combine(
            context.Config.ArtifactDirectory,
            context.Config.OutputMode + "-layout-audit.json");
        PrototypeAuditRow[] auditRows = ReadAuditRows(auditPath);
        ManifestRecord[] manifest = ReadJsonLines<ManifestRecord>(context.Config.ManifestPath, "manifest");
        TranslationRecord[] translations = ReadJsonLines<TranslationRecord>(context.Config.TranslationPath, "translation");
        IReadOnlyDictionary<string, ManifestRecord> manifestById = manifest.ToDictionary(row => row.RecordId, StringComparer.Ordinal);
        IReadOnlyDictionary<string, TranslationRecord> translationById = translations.ToDictionary(row => row.RecordId, StringComparer.Ordinal);

        var reportRows = new List<object>();
        int replacedRecords = 0;
        using (var database = new Database(false, true))
        {
            ReadWorkingDrawing(database, context.Config.WorkingPath, context.Config.ArtifactDirectory);
            Database originalWorkingDatabase = HostApplicationServices.WorkingDatabase;
            try
            {
                HostApplicationServices.WorkingDatabase = database;
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    DenseRegion[] denseRegions = BuildDenseRegions(auditRows, manifestById);
                    foreach (IGrouping<string, DenseRegion> definition in denseRegions.GroupBy(region => region.DefinitionName, StringComparer.Ordinal))
                    {
                        DenseRegion[] ordered = definition.OrderBy(region => region.Left).ToArray();
                        for (int index = 0; index < ordered.Length; index++)
                        {
                            DenseRegion region = ordered[index];
                            double right = NarrativeRegionRightBoundary.Resolve(
                                region.Left,
                                region.Bottom,
                                region.Top,
                                region.MedianHeight,
                                region.Rows.Select(row => row.SourceBounds.Right).ToArray(),
                                ordered.Skip(index + 1)
                                    .Select(candidate => new NarrativeRegionEnvelope(
                                        candidate.Left,
                                        candidate.Bottom,
                                        candidate.Top))
                                    .ToArray());
                            replacedRecords += ComposeDenseRegion(
                                database,
                                transaction,
                                region,
                                right,
                                manifestById,
                                translationById,
                                reportRows);
                        }
                    }

                    replacedRecords += ComposeFragmentedTitles(
                        database,
                        transaction,
                        auditRows,
                        manifestById,
                        translationById,
                        denseRegions,
                        reportRows);
                    if (context.Config.TargetLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                    replacedRecords += ApplyBilingualFixedLabels(
                        database,
                        transaction,
                        auditRows,
                        manifestById,
                        translationById,
                        reportRows);
                    transaction.Commit();
                }

                Directory.CreateDirectory(Path.GetDirectoryName(context.Config.OutputPath)!);
                SaveOutput(database, context.Config.OutputPath);
            }
            finally
            {
                HostApplicationServices.WorkingDatabase = originalWorkingDatabase;
            }
        }
        AtomicFile.WriteUtf8(
            Path.Combine(context.Config.ArtifactDirectory, "logical-flow-report.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = "1.0",
                prototype = true,
                replacedRecords,
                composedObjects = reportRows.Count,
                rows = reportRows
            }, JsonDefaults.Options));
        return replacedRecords;
    }

    private static int ApplyBilingualFixedLabels(
        Database database,
        Transaction transaction,
        IReadOnlyList<PrototypeAuditRow> auditRows,
        IReadOnlyDictionary<string, ManifestRecord> manifestById,
        IReadOnlyDictionary<string, TranslationRecord> translationById,
        List<object> reportRows)
    {
        PrototypeAuditRow[] eligibleRows = auditRows
            .Where(row => manifestById.TryGetValue(row.RecordId, out ManifestRecord? manifest) &&
                          translationById.ContainsKey(row.RecordId) &&
                          manifest.ObjectType is "AcDbText" or "AcDbMText")
            .ToArray();
        BilingualFixedLabelSelection selection = BilingualFixedLabelPolicy.Select(
            eligibleRows.Select(row =>
            {
                ManifestRecord manifest = manifestById[row.RecordId];
                return new BilingualFixedLabelSample(
                    row.RecordId,
                    manifest.RawText,
                    RestoredText(manifest, translationById[row.RecordId]),
                    manifest.ObjectType,
                    row.DefinitionName,
                    row.SourceBounds,
                    manifest.Properties.Height);
            }).ToArray());
        IReadOnlyDictionary<string, PrototypeAuditRow> rowById = eligibleRows
            .ToDictionary(row => row.RecordId, StringComparer.Ordinal);
        int changed = 0;

        foreach ((string recordId, string existingEnglish) in selection.MixedObjectEnglishTextById)
        {
            if (!rowById.TryGetValue(recordId, out PrototypeAuditRow? row))
            {
                continue;
            }
            Entity? entity = TryResolveEntity(database, transaction, row.NewHandle) ??
                             TryResolveEntity(database, transaction, row.OldHandle);
            switch (entity)
            {
                case DBText text:
                    text.TextString = existingEnglish;
                    break;
                case MText text:
                    text.Contents = existingEnglish;
                    break;
                default:
                    continue;
            }
            changed++;
            reportRows.Add(new
            {
                kind = "existing-bilingual-inline-english-kept",
                recordId,
                row.DefinitionName,
                handle = entity.Handle.ToString()
            });
        }

        foreach (string recordId in selection.SuppressChineseIds)
        {
            if (!rowById.TryGetValue(recordId, out PrototypeAuditRow? row))
            {
                continue;
            }
            Entity? entity = TryResolveEntity(database, transaction, row.NewHandle) ??
                             TryResolveEntity(database, transaction, row.OldHandle);
            if (entity is null)
            {
                continue;
            }
            entity.Erase();
            changed++;
            reportRows.Add(new
            {
                kind = "existing-bilingual-separate-english-kept",
                recordId,
                row.DefinitionName,
                removedHandle = entity.Handle.ToString()
            });
        }

        return changed;
    }

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
            string logPath = Path.Combine(artifactDirectory, $".compose-dxf-import.{Guid.NewGuid():N}.log");
            try
            {
                database.DxfIn(path, logPath);
            }
            finally
            {
                if (File.Exists(logPath))
                {
                    File.Delete(logPath);
                }
            }
            return;
        }
        throw new CommandProtocolException("unsupported_input_format", "compose input must be DWG or DXF.");
    }

    private static void SaveOutput(Database database, string outputPath)
    {
        string extension = Path.GetExtension(outputPath);
        if (extension.Equals(".dwg", StringComparison.OrdinalIgnoreCase))
        {
            database.SaveAs(outputPath, true, database.OriginalFileVersion, database.SecurityParameters);
            return;
        }
        if (extension.Equals(".dxf", StringComparison.OrdinalIgnoreCase))
        {
            database.DxfOut(outputPath, 16, database.OriginalFileVersion);
            return;
        }
        throw new CommandProtocolException("unsupported_output_format", "compose output must be DWG or DXF.");
    }

    private static DenseRegion[] BuildDenseRegions(
        IReadOnlyList<PrototypeAuditRow> auditRows,
        IReadOnlyDictionary<string, ManifestRecord> manifestById)
    {
        var regions = new List<DenseRegion>();
        var authoritativeClaims = new HashSet<string>(StringComparer.Ordinal);

        foreach (IGrouping<string, PrototypeAuditRow> group in auditRows
                     .Where(row => NarrativeCandidatePolicy.CanUseAuthoritativeSelector(row.RegionId) &&
                         manifestById.ContainsKey(row.RecordId))
                     .GroupBy(row => row.DefinitionName, StringComparer.Ordinal))
        {
            PrototypeAuditRow[] rows = group.ToArray();
            double medianHeight = Median(rows.Select(row => manifestById[row.RecordId].Properties.Height));
            IReadOnlyDictionary<string, PrototypeAuditRow> rowById = rows
                .ToDictionary(row => row.RecordId, StringComparer.Ordinal);
            FragmentedNarrativeSample[] samples = rows.Select(row =>
            {
                ManifestRecord manifest = manifestById[row.RecordId];
                return new FragmentedNarrativeSample(
                    row.RecordId,
                    row.SourceBounds,
                    manifest.RawText,
                    manifest.ObjectType is "AcDbText" or "AcDbMText");
            }).ToArray();
            NarrativeOccupancyGroup[] proseGroups =
                AuthoritativeNarrativeSelector.SelectPanelGroups(samples, medianHeight);
            for (int groupIndex = 0; groupIndex < proseGroups.Length; groupIndex++)
            {
                PrototypeAuditRow[] panelRows = proseGroups[groupIndex].MemberIds
                    .Select(id => rowById[id])
                    .ToArray();
                regions.Add(CreateDenseRegion(
                    group.Key,
                    $"authoritative-narrative-{groupIndex + 1}",
                    "existing-note-column",
                    panelRows,
                    medianHeight));
            }
            foreach (PrototypeAuditRow row in rows)
            {
                authoritativeClaims.Add(row.RecordId);
            }
        }

        foreach (IGrouping<string, PrototypeAuditRow> definition in auditRows
                     .Where(row => manifestById.ContainsKey(row.RecordId) &&
                         NarrativeCandidatePolicy.CanFeedGenericDetector(
                             row.RegionId,
                             authoritativeClaims.Contains(row.RecordId)))
                     .GroupBy(row => row.DefinitionName, StringComparer.Ordinal))
        {
            PrototypeAuditRow[] candidateRows = definition.ToArray();
            PrototypeAuditRow[] detectionRows = candidateRows
                .Where(row => string.IsNullOrEmpty(row.RegionId))
                .ToArray();
            if (detectionRows.Length == 0)
            {
                continue;
            }

            double medianHeight = Median(detectionRows.Select(row => manifestById[row.RecordId].Properties.Height));
            FragmentedNarrativeSample[] detectionSamples = detectionRows.Select(row =>
            {
                ManifestRecord manifest = manifestById[row.RecordId];
                return new FragmentedNarrativeSample(
                    row.RecordId,
                    row.SourceBounds,
                    manifest.RawText,
                    manifest.ObjectType is "AcDbText" or "AcDbMText");
            }).ToArray();
            FragmentedNarrativeSample[] expansionSamples = candidateRows.Select(row =>
            {
                ManifestRecord manifest = manifestById[row.RecordId];
                return new FragmentedNarrativeSample(
                    row.RecordId,
                    row.SourceBounds,
                    manifest.RawText,
                    manifest.ObjectType is "AcDbText" or "AcDbMText");
            }).ToArray();
            IReadOnlyDictionary<string, PrototypeAuditRow> rowById = candidateRows
                .ToDictionary(row => row.RecordId, StringComparer.Ordinal);
            IReadOnlyDictionary<string, FragmentedNarrativeSample> sampleById = expansionSamples
                .ToDictionary(sample => sample.Id, StringComparer.Ordinal);
            FragmentedNarrativeGroup[] seedGroups =
                FragmentedNarrativeDetector.DetectGroups(detectionSamples, medianHeight);
            FragmentedNarrativeGroup[] secondPassGroups = seedGroups.Length >= 3
                ? FragmentedNarrativeDetector.DetectGroups(expansionSamples, medianHeight)
                : seedGroups;
            FragmentedNarrativeGroup[] detected = NarrativePanelPlanner.SelectCompletePanelColumns(
                seedGroups,
                secondPassGroups,
                medianHeight);
            var claimed = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < detected.Length; index++)
            {
                FragmentedNarrativeGroup group = detected[index];
                string[] candidateIds = FragmentedNarrativeRegionExpander.Expand(group, expansionSamples, medianHeight);
                string[] expandedIds = FragmentedNarrativeTableRowFilter
                    .KeepNarrativeMembers(candidateIds.Select(id => sampleById[id]).ToArray(), medianHeight)
                    .Where(id => claimed.Add(id))
                    .ToArray();
                if (expandedIds.Length < 12)
                {
                    continue;
                }
                PrototypeAuditRow[] members = expandedIds.Select(id => rowById[id]).ToArray();
                regions.Add(CreateDenseRegion(
                    definition.Key,
                    $"fragmented-narrative-{index + 1}",
                    "detected-fragmented-narrative",
                    members,
                    Median(members.Select(row => manifestById[row.RecordId].Properties.Height))));
            }
        }

        return regions
            .OrderBy(region => region.DefinitionName, StringComparer.Ordinal)
            .ThenByDescending(region => region.Top)
            .ThenBy(region => region.Left)
            .ToArray();
    }

    private static DenseRegion CreateDenseRegion(
        string definitionName,
        string regionId,
        string selectionKind,
        IReadOnlyList<PrototypeAuditRow> rows,
        double medianHeight) =>
        new(
            definitionName,
            regionId,
            selectionKind,
            rows,
            Percentile(rows.Select(row => row.SourceBounds.Left), 0.05),
            rows.Max(row => row.SourceBounds.Top),
            rows.Min(row => row.SourceBounds.Bottom),
            medianHeight);

    private static int ComposeDenseRegion(
        Database database,
        Transaction transaction,
        DenseRegion region,
        double right,
        IReadOnlyDictionary<string, ManifestRecord> manifestById,
        IReadOnlyDictionary<string, TranslationRecord> translationById,
        List<object> reportRows)
    {
        BilingualNarrativeSelection bilingual = BilingualNarrativePolicy.Select(
            region.Rows
                .Where(row => manifestById.ContainsKey(row.RecordId))
                .Select(row =>
                {
                    ManifestRecord manifest = manifestById[row.RecordId];
                    return new BilingualNarrativeSample(
                        row.RecordId,
                        manifest.RawText,
                        manifest.ObjectType);
                })
                .ToArray());
        if (bilingual.PreferExistingEnglish)
        {
            return RestoreExistingEnglishNarrative(
                database,
                transaction,
                region,
                bilingual,
                manifestById,
                translationById,
                reportRows);
        }

        LogicalTextFragment[] fragments = region.Rows
            .Where(row => translationById.ContainsKey(row.RecordId))
            .Select(row => new LogicalTextFragment(
                row.RecordId,
                row.SourceBounds,
                RestoredText(manifestById[row.RecordId], translationById[row.RecordId])))
            .ToArray();
        LogicalComposedRow[] logicalRows = LogicalTextComposer.ComposeRows(fragments, region.MedianHeight);
        if (logicalRows.Length == 0)
        {
            return 0;
        }

        LogicalComposedRow[][] segments = LogicalTextSegmenter.SplitAtSourceGaps(logicalRows);
        IReadOnlyDictionary<string, PrototypeAuditRow> auditById = region.Rows
            .ToDictionary(row => row.RecordId, StringComparer.Ordinal);
        double width = right - region.Left;
        int replaced = 0;
        for (int segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
        {
            LogicalComposedRow[] segment = segments[segmentIndex];
            PrototypeAuditRow[] segmentRows = segment
                .SelectMany(row => row.MemberIds)
                .Distinct(StringComparer.Ordinal)
                .Select(id => auditById[id])
                .ToArray();
            Entity[] oldEntities = ResolveEntities(database, transaction, segmentRows);
            if (oldEntities.Length == 0)
            {
                throw new CommandProtocolException("compose_missing_entities", $"No candidate entities resolved for {region.RegionId} segment {segmentIndex + 1}.");
            }
            EnsureSingleOwner(oldEntities, $"{region.RegionId} segment {segmentIndex + 1}");

            double sourceTop = segments.Length == 1 ? region.Top : segment.Max(row => row.SourceBounds.Top);
            double sourceBottom = segments.Length == 1 ? region.Bottom : segment.Min(row => row.SourceBounds.Bottom);
            Rect2[] currentBounds = oldEntities
                .Select(CadLayoutGeometry.TryLayoutBounds)
                .Where(bounds => bounds is not null)
                .Select(bounds => new Rect2(
                    bounds!.MinX,
                    bounds.MinY,
                    bounds.MaxX,
                    bounds.MaxY))
                .ToArray();
            Rect2 placementEnvelope = LogicalCompositionPlacementPolicy.SelectVerticalEnvelope(
                new Rect2(region.Left, sourceBottom, right, sourceTop),
                currentBounds,
                oldEntities.Length);
            double top = placementEnvelope.Top;
            double bottom = placementEnvelope.Bottom;
            double availableHeight = top - bottom;
            Entity representative = RepresentativeEntity(oldEntities);
            MText replacement = CreateMText(
                database,
                transaction,
                representative,
                JoinRows(segment),
                AttachmentPoint.TopLeft,
                new Point3d(region.Left, top, EntityZ(representative)),
                width,
                region.MedianHeight);
            double selectedScale = FitHeight(replacement, region.MedianHeight, availableHeight, MinimumBodyScale);
            double actualHeight = CadLayoutGeometry.ActualHeight(replacement);

            if (!LogicalCompositionFitPolicy.ShouldReplace(actualHeight, availableHeight))
            {
                replacement.Erase();
                reportRows.Add(new
                {
                    kind = $"{region.SelectionKind}-preserved",
                    region.DefinitionName,
                    region.RegionId,
                    segmentIndex = segmentIndex + 1,
                    segmentCount = segments.Length,
                    sourceObjects = segmentRows.Length,
                    logicalRows = segment.Length,
                    left = region.Left,
                    right,
                    top,
                    bottom,
                    textHeight = replacement.TextHeight,
                    scale = selectedScale,
                    actualHeight,
                    availableHeight,
                    overflowReplacementSkipped = true
                });
                continue;
            }

            foreach (Entity entity in oldEntities)
            {
                entity.Erase();
            }

            reportRows.Add(new
            {
                kind = region.SelectionKind,
                region.DefinitionName,
                region.RegionId,
                segmentIndex = segmentIndex + 1,
                segmentCount = segments.Length,
                sourceObjects = segmentRows.Length,
                logicalRows = segment.Length,
                left = region.Left,
                right,
                top,
                bottom,
                textHeight = replacement.TextHeight,
                scale = selectedScale,
                actualHeight,
                availableHeight,
                sourceColors = oldEntities
                    .GroupBy(entity => new { entity.Layer, entity.Color.ColorIndex })
                    .Select(group => new { group.Key.Layer, group.Key.ColorIndex, count = group.Count() })
                    .OrderByDescending(group => group.count)
                    .ToArray(),
                newHandle = replacement.Handle.ToString()
            });
            replaced += oldEntities.Length;
        }
        return replaced;
    }

    private static int RestoreExistingEnglishNarrative(
        Database database,
        Transaction transaction,
        DenseRegion region,
        BilingualNarrativeSelection selection,
        IReadOnlyDictionary<string, ManifestRecord> manifestById,
        IReadOnlyDictionary<string, TranslationRecord> translationById,
        List<object> reportRows)
    {
        IReadOnlyDictionary<string, PrototypeAuditRow> auditById = region.Rows
            .ToDictionary(row => row.RecordId, StringComparer.Ordinal);
        PrototypeAuditRow[] englishRows = selection.ExistingEnglishIds
            .Where(id => auditById.ContainsKey(id) && translationById.ContainsKey(id))
            .Select(id => auditById[id])
            .ToArray();
        PrototypeAuditRow[] duplicateRows = selection.DuplicateChineseNarrativeIds
            .Where(auditById.ContainsKey)
            .Select(id => auditById[id])
            .ToArray();
        Entity[] oldEnglish = ResolveEntities(database, transaction, englishRows);
        Entity[] duplicateChineseTranslations = ResolveEntities(database, transaction, duplicateRows);
        EnsureSingleOwner(oldEnglish, $"{region.RegionId} existing English narrative");

        var rebuilt = new List<object>();
        for (int index = 0; index < englishRows.Length; index++)
        {
            PrototypeAuditRow row = englishRows[index];
            ManifestRecord manifest = manifestById[row.RecordId];
            Entity source = oldEnglish[index];
            double sourceHeight = Math.Max(1e-6, manifest.Properties.Height);
            double width = Math.Max(row.SourceBounds.Width, sourceHeight * 8);
            MText replacement = CreateMText(
                database,
                transaction,
                source,
                RestoredText(manifest, translationById[row.RecordId]),
                AttachmentPoint.TopLeft,
                new Point3d(row.SourceBounds.Left, row.SourceBounds.Top, EntityZ(source)),
                width,
                sourceHeight);
            double scale = FitHeight(
                replacement,
                sourceHeight,
                Math.Max(row.SourceBounds.Height, sourceHeight),
                0.75);
            rebuilt.Add(new
            {
                recordId = row.RecordId,
                sourceBounds = row.SourceBounds,
                textHeight = replacement.TextHeight,
                scale,
                newHandle = replacement.Handle.ToString()
            });
        }

        foreach (Entity entity in oldEnglish.Concat(duplicateChineseTranslations).DistinctBy(entity => entity.ObjectId))
        {
            entity.Erase();
        }

        reportRows.Add(new
        {
            kind = "existing-bilingual-english-restored",
            region.DefinitionName,
            region.RegionId,
            sourceObjects = oldEnglish.Length + duplicateChineseTranslations.Length,
            restoredEnglishObjects = oldEnglish.Length,
            removedDuplicateChineseNarrativeObjects = duplicateChineseTranslations.Length,
            rebuilt
        });
        return oldEnglish
            .Concat(duplicateChineseTranslations)
            .Select(entity => entity.ObjectId)
            .Distinct()
            .Count();
    }

    private static int ComposeFragmentedTitles(
        Database database,
        Transaction transaction,
        IReadOnlyList<PrototypeAuditRow> auditRows,
        IReadOnlyDictionary<string, ManifestRecord> manifestById,
        IReadOnlyDictionary<string, TranslationRecord> translationById,
        IReadOnlyList<DenseRegion> denseRegions,
        List<object> reportRows)
    {
        // Spaced table headers are authored one character per text entity (项|目,
        // 做|法|名|称, 室|内|外). They must merge into a single term whatever their
        // size: replacing every fragment with the whole phrase renders headers as
        // "ItemItem" or four overlapping "Method name" copies.
        var candidates = auditRows
            .Where(row => string.IsNullOrEmpty(row.RegionId) && manifestById.TryGetValue(row.RecordId, out ManifestRecord? record) &&
                translationById.ContainsKey(row.RecordId) && IsSingleCjk(record.RawText))
            .Select(row => new TitleCandidate(row, manifestById[row.RecordId]))
            .ToArray();

        int replaced = 0;
        foreach (TitleCandidate[] run in ClusterTitles(candidates).Select(AdjacentRun))
        {
            TitleCandidate[] ordered = run;
            if (ordered.Length < 2)
            {
                continue;
            }

            PrototypeAuditRow[] titleRows = ordered.Select(candidate => candidate.Row).ToArray();
            Entity[] oldEntities = ResolveEntities(database, transaction, titleRows);
            EnsureSingleOwner(oldEntities, $"title {ordered[0].Row.DefinitionName}");
            Rect2 bounds = Union(titleRows.Select(row => row.SourceBounds));
            double sourceHeight = Median(ordered.Select(candidate => candidate.Manifest.Properties.Height));
            string contents = LogicalTextComposer.ComposeLine(ordered.Select(candidate =>
                RestoredText(candidate.Manifest, translationById[candidate.Manifest.RecordId])));
            Entity representative = RepresentativeEntity(oldEntities);
            MText replacement = CreateMText(
                database,
                transaction,
                representative,
                contents,
                AttachmentPoint.MiddleCenter,
                new Point3d(bounds.Center.X, bounds.Center.Y, EntityZ(representative)),
                Math.Max(bounds.Width, sourceHeight),
                sourceHeight);
            double scale = FitWidth(replacement, sourceHeight, bounds.Width, 0.65);
            foreach (Entity entity in oldEntities)
            {
                entity.Erase();
            }

            reportRows.Add(new
            {
                kind = "fragmented-title",
                definitionName = ordered[0].Row.DefinitionName,
                sourceObjects = oldEntities.Length,
                contents,
                textHeight = replacement.TextHeight,
                scale,
                sourceBounds = bounds,
                newHandle = replacement.Handle.ToString()
            });
            replaced += oldEntities.Length;
        }
        return replaced;
    }

    private static MText CreateMText(
        Database database,
        Transaction transaction,
        Entity source,
        string contents,
        AttachmentPoint attachment,
        Point3d location,
        double width,
        double textHeight)
    {
        var owner = (BlockTableRecord)transaction.GetObject(source.OwnerId, OpenMode.ForWrite, false);
        var replacement = new MText();
        replacement.SetDatabaseDefaults(database);
        replacement.Contents = NarrativeTargetFontPolicy.ApplyLatinWrapper(contents);
        replacement.Attachment = attachment;
        replacement.Location = location;
        replacement.Width = Math.Max(textHeight, width);
        replacement.TextHeight = textHeight;
        replacement.LineSpacingStyle = LineSpacingStyle.AtLeast;
        replacement.LineSpacingFactor = 0.90;
        replacement.LayerId = source.LayerId;
        replacement.Color = source.Color;
        replacement.LineWeight = source.LineWeight;
        replacement.LinetypeId = source.LinetypeId;
        replacement.LinetypeScale = source.LinetypeScale;
        replacement.Transparency = source.Transparency;
        if (source is MText sourceMText)
        {
            replacement.TextStyleId = sourceMText.TextStyleId;
            replacement.Rotation = sourceMText.Rotation;
            replacement.Normal = sourceMText.Normal;
        }
        else if (source is DBText sourceText)
        {
            replacement.TextStyleId = sourceText.TextStyleId;
            replacement.Rotation = sourceText.Rotation;
            replacement.Normal = sourceText.Normal;
        }
        owner.AppendEntity(replacement);
        transaction.AddNewlyCreatedDBObject(replacement, true);
        return replacement;
    }

    private static double FitHeight(MText text, double sourceHeight, double availableHeight, double floor)
    {
        double selected = floor;
        foreach (double scale in CadLayoutGeometry.Steps(1, floor, 0.01))
        {
            text.TextHeight = sourceHeight * scale;
            selected = scale;
            if (CadLayoutGeometry.ActualHeight(text) <= availableHeight * 0.98)
            {
                break;
            }
        }
        return selected;
    }

    private static double FitWidth(MText text, double sourceHeight, double availableWidth, double floor)
    {
        double selected = floor;
        foreach (double scale in CadLayoutGeometry.Steps(1, floor, 0.05))
        {
            text.TextHeight = sourceHeight * scale;
            selected = scale;
            double width;
            try { width = Math.Max(text.TextHeight, text.ActualWidth); }
            catch (Autodesk.AutoCAD.Runtime.Exception) { width = availableWidth; }
            if (width <= availableWidth * 0.98)
            {
                break;
            }
        }
        return selected;
    }

    private static Entity[] ResolveEntities(Database database, Transaction transaction, IReadOnlyList<PrototypeAuditRow> rows)
    {
        var entities = new List<Entity>(rows.Count);
        var seen = new HashSet<ObjectId>();
        var missing = new List<string>();
        foreach (PrototypeAuditRow row in rows)
        {
            Entity? entity = TryResolveEntity(database, transaction, row.NewHandle) ??
                TryResolveEntity(database, transaction, row.OldHandle);
            if (entity is null)
            {
                missing.Add($"{row.RecordId}:{row.NewHandle}/{row.OldHandle}");
                continue;
            }
            if (seen.Add(entity.ObjectId))
            {
                entities.Add(entity);
            }
        }
        if (missing.Count > 0)
        {
            throw new CommandProtocolException(
                "compose_handle_mismatch",
                $"{missing.Count} audited handles did not resolve. First: {string.Join(", ", missing.Take(5))}");
        }
        return entities.ToArray();
    }

    private static Entity? TryResolveEntity(Database database, Transaction transaction, string textHandle)
    {
        if (!long.TryParse(textHandle, System.Globalization.NumberStyles.HexNumber, null, out long value))
        {
            return null;
        }
        try
        {
            ObjectId id = database.GetObjectId(false, new Handle(value), 0);
            return id.IsNull ? null : transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return null;
        }
    }

    private static void EnsureSingleOwner(IReadOnlyList<Entity> entities, string label)
    {
        if (entities.Count == 0 || entities.Any(entity => entity.OwnerId != entities[0].OwnerId))
        {
            throw new CommandProtocolException("compose_owner_mismatch", $"{label} does not belong to one text container.");
        }
    }

    private static Entity RepresentativeEntity(IReadOnlyList<Entity> entities) => entities
        .GroupBy(entity => new { entity.Layer, entity.Color.ColorIndex })
        .OrderByDescending(group => group.Count())
        .ThenBy(group => group.Key.Layer, StringComparer.Ordinal)
        .ThenBy(group => group.Key.ColorIndex)
        .First()
        .First();

    private static string RestoredText(ManifestRecord manifest, TranslationRecord translation) =>
        TranslationValidator.RestoreProtectedTokensForOutput(translation.TranslatedText, manifest.ProtectedTokens);

    private static string JoinRows(IReadOnlyList<LogicalComposedRow> rows)
    {
        var parts = new List<string>(rows.Count);
        foreach (LogicalComposedRow row in rows)
        {
            if (row.ParagraphGapBefore && parts.Count > 0)
            {
                parts.Add(string.Empty);
            }
            parts.Add(row.Text);
        }
        return string.Join(@"\P", parts);
    }

    private static double EntityZ(Entity entity) => entity switch
    {
        MText text => text.Location.Z,
        DBText text => text.Position.Z,
        _ => 0
    };

    private static bool IsSingleCjk(string value)
    {
        string trimmed = value.Trim();
        return trimmed.Length == 1 && SingleCjk().IsMatch(trimmed);
    }

    private static TitleCandidate[][] ClusterTitles(IEnumerable<TitleCandidate> values)
    {
        var result = new List<TitleCandidate[]>();
        foreach (IGrouping<string, TitleCandidate> definition in values.GroupBy(value => value.Row.DefinitionName, StringComparer.Ordinal))
        {
            var current = new List<TitleCandidate>();
            foreach (TitleCandidate candidate in definition.OrderBy(value => value.Row.SourceBounds.Center.Y))
            {
                if (current.Count > 0)
                {
                    double medianY = Median(current.Select(value => value.Row.SourceBounds.Center.Y));
                    double medianHeight = Median(current.Select(value => value.Manifest.Properties.Height));
                    if (Math.Abs(candidate.Row.SourceBounds.Center.Y - medianY) > medianHeight * 0.40)
                    {
                        result.Add(current.OrderBy(value => value.Row.SourceBounds.Left).ToArray());
                        current.Clear();
                    }
                }
                current.Add(candidate);
            }
            if (current.Count > 0)
            {
                result.Add(current.OrderBy(value => value.Row.SourceBounds.Left).ToArray());
            }
        }
        return result.ToArray();
    }

    // Within one row band keep only genuine neighbours: the gap between two
    // fragments must stay within a couple of text heights, so two separate
    // headers on the same row are not merged into each other.
    private static TitleCandidate[] AdjacentRun(TitleCandidate[] clustered)
    {
        if (clustered.Length < 2)
        {
            return clustered;
        }

        var best = new List<TitleCandidate> { clustered[0] };
        var current = new List<TitleCandidate> { clustered[0] };
        foreach (TitleCandidate candidate in clustered.Skip(1))
        {
            double medianHeight = Median(current.Select(value => value.Manifest.Properties.Height));
            double gap = candidate.Row.SourceBounds.Left - current[^1].Row.SourceBounds.Right;
            current = gap <= medianHeight * 2.2
                ? current.Append(candidate).ToList()
                : new List<TitleCandidate> { candidate };
            if (current.Count > best.Count)
            {
                best = current;
            }
        }
        return best.ToArray();
    }

    private static Rect2 Union(IEnumerable<Rect2> bounds)
    {
        Rect2[] values = bounds.ToArray();
        return new Rect2(
            values.Min(value => value.Left),
            values.Min(value => value.Bottom),
            values.Max(value => value.Right),
            values.Max(value => value.Top));
    }

    private static double Median(IEnumerable<double> values) => Percentile(values, 0.50);

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        double[] ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
        {
            throw new InvalidOperationException("Cannot calculate a percentile for an empty sequence.");
        }
        int index = (int)Math.Round((ordered.Length - 1) * percentile);
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }

    private static T ReadJson<T>(string path, string name)
    {
        if (!File.Exists(path))
        {
            throw new CommandProtocolException($"missing_{name.Replace(' ', '_')}", $"{name} file does not exist.");
        }
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonDefaults.Options)
                ?? throw new JsonException("Document cannot be null.");
        }
        catch (JsonException exception)
        {
            throw new CommandProtocolException($"invalid_{name.Replace(' ', '_')}", $"{name} must be valid JSON.", exception);
        }
    }

    private static PrototypeAuditRow[] ReadAuditRows(string path)
    {
        if (!File.Exists(path))
        {
            throw new CommandProtocolException("missing_layout_audit", "layout audit file does not exist.");
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.GetProperty("texts").EnumerateArray().Select(row =>
            {
                JsonElement bounds = row.GetProperty("sourceBounds");
                return new PrototypeAuditRow(
                    row.GetProperty("recordId").GetString() ?? string.Empty,
                    row.GetProperty("definitionName").GetString() ?? string.Empty,
                    row.GetProperty("regionId").GetString() ?? string.Empty,
                    row.GetProperty("oldHandle").GetString() ?? string.Empty,
                    row.GetProperty("newHandle").GetString() ?? string.Empty,
                    new Rect2(
                        bounds.GetProperty("left").GetDouble(),
                        bounds.GetProperty("bottom").GetDouble(),
                        bounds.GetProperty("right").GetDouble(),
                        bounds.GetProperty("top").GetDouble()));
            }).ToArray();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new CommandProtocolException("invalid_layout_audit", "layout audit must contain explicit source bounds.", exception);
        }
    }

    private static T[] ReadJsonLines<T>(string path, string name)
    {
        if (!File.Exists(path))
        {
            throw new CommandProtocolException($"missing_{name}", $"{name} file does not exist.");
        }
        try
        {
            return File.ReadLines(path)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonSerializer.Deserialize<T>(line, JsonDefaults.Options) ?? throw new JsonException("Record cannot be null."))
                .ToArray();
        }
        catch (JsonException exception)
        {
            throw new CommandProtocolException($"invalid_{name}", $"{name} must be valid JSONL.", exception);
        }
    }

    [GeneratedRegex("^[\\u3400-\\u9fff]$", RegexOptions.CultureInvariant)]
    private static partial Regex SingleCjk();

    private sealed record DenseRegion(
        string DefinitionName,
        string RegionId,
        string SelectionKind,
        IReadOnlyList<PrototypeAuditRow> Rows,
        double Left,
        double Top,
        double Bottom,
        double MedianHeight);

    private sealed record PrototypeAuditRow(
        string RecordId,
        string DefinitionName,
        string RegionId,
        string OldHandle,
        string NewHandle,
        Rect2 SourceBounds);

    private sealed record TitleCandidate(PrototypeAuditRow Row, ManifestRecord Manifest);
}

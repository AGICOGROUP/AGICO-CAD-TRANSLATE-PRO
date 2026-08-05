using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using CadTranslation.Contracts;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

internal sealed record GlobalCorrectionTarget(
    ManifestRecord Manifest,
    string RestoredText);

internal static class GlobalCollisionCorrection
{
    internal static LayoutAdjustment[] Apply(
        Database database,
        Transaction transaction,
        CadLayoutBaseline baseline,
        LayoutOptimizationResult optimization,
        LayoutAuditReport audit,
        IReadOnlyDictionary<string, GlobalCorrectionTarget> targets)
    {
        string[] selected = LayoutCorrectionPolicy.SelectAdjustedTextOverlapRecords(
            audit.Risks.Select(risk => new LayoutCorrectionCandidate(
                risk.RecordId,
                risk.OtherRecordId,
                ParseCode(risk.Code),
                ParseLevel(risk.Level))).ToArray(),
            optimization.Adjustments.Select(adjustment => adjustment.RecordId).ToArray());
        if (selected.Length == 0)
        {
            int highTextRiskCount = audit.Risks.Count(risk =>
                string.Equals(risk.Code, "text-overlap", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(risk.Level, "high", StringComparison.OrdinalIgnoreCase));
            if (highTextRiskCount > 0)
            {
                throw new CommandProtocolException(
                    "correction_selection_empty",
                    $"Found {highTextRiskCount} high text overlaps but selected no adjusted records from {optimization.Adjustments.Count} adjustments.");
            }
            return [];
        }

        Dictionary<string, CadLayoutText> topologyByRecord = baseline.Definitions
            .SelectMany(definition => definition.Texts)
            .ToDictionary(text => text.RecordId, StringComparer.Ordinal);
        Dictionary<string, Rect2> slotByRecord = AllocateCorrectionSlots(baseline);
        Dictionary<string, LayoutAuditTextRow> auditByRecord = audit.Texts
            .ToDictionary(text => text.RecordId, StringComparer.Ordinal);
        var corrections = new List<LayoutAdjustment>();
        var handled = new HashSet<string>(StringComparer.Ordinal);

        foreach (IGrouping<string, CadLayoutText> group in selected
                     .Select(recordId => topologyByRecord[recordId])
                     .Where(text => text.Region?.Kind == LayoutRegionKind.NoteColumn)
                     .GroupBy(
                         text => $"{text.DefinitionName}\u001f{text.Region!.Id}",
                         StringComparer.Ordinal))
        {
            CadLayoutText seed = group.First();
            CadDefinitionTopology definition = baseline.Definitions.Single(item =>
                string.Equals(item.Name, seed.DefinitionName, StringComparison.Ordinal));
            CadLayoutText[] members = definition.Texts
                .Where(text =>
                    text.Region?.Kind == LayoutRegionKind.NoteColumn &&
                    string.Equals(text.Region.Id, seed.Region!.Id, StringComparison.Ordinal) &&
                    targets.ContainsKey(text.RecordId) &&
                    auditByRecord.ContainsKey(text.RecordId))
                .OrderByDescending(text => text.Source.Bounds.Top)
                .ThenBy(text => text.Source.Bounds.Left)
                .ToArray();
            LayoutRegion correctionRegion = seed.Region! with
            {
                Bounds = NarrativeOccupancyEnvelope.ConstrainToSourceVerticalEnvelope(
                    seed.Region.Bounds,
                    members.Select(member => member.Source.Bounds).ToArray())
            };
            CadLayoutText[] correctionMembers = members
                .Select(member => member with { Region = correctionRegion })
                .ToArray();
            LayoutTargetSnapshot[] snapshots = correctionMembers
                .Select(member => CreateSnapshot(
                    database,
                    transaction,
                    member,
                    auditByRecord,
                    targets))
                .ToArray();
            corrections.AddRange(NoteColumnLayout.Apply(
                database,
                transaction,
                snapshots,
                correctionMembers,
                LayoutCorrectionPolicy.MinimumGroupCorrectionScale));
            foreach (CadLayoutText member in members)
            {
                handled.Add(member.RecordId);
            }
        }

        foreach (string recordId in selected)
        {
            if (handled.Contains(recordId))
            {
                continue;
            }

            if (!topologyByRecord.TryGetValue(recordId, out CadLayoutText? topology))
                throw new CommandProtocolException("correction_topology_missing", $"No topology for {recordId}.");
            if (!slotByRecord.TryGetValue(recordId, out Rect2 allowed))
                throw new CommandProtocolException("correction_slot_missing", $"No source slot for {recordId}.");
            if (!auditByRecord.TryGetValue(recordId, out LayoutAuditTextRow? auditText))
                throw new CommandProtocolException("correction_audit_text_missing", $"No audit text for {recordId}.");
            if (!targets.TryGetValue(recordId, out GlobalCorrectionTarget? target))
                throw new CommandProtocolException("correction_target_missing", $"No translated target for {recordId}.");

            ObjectId objectId = ResolveObjectId(database, auditText.NewHandle);
            Entity entity = (Entity)transaction.GetObject(objectId, OpenMode.ForWrite, false);
            MoveToSourceAnchor(entity, topology, target);
            string correctionText = entity is MText currentMText
                ? LayoutTextNormalization.RemoveGeneratedWidthWrapper(currentMText.Contents)
                : target.RestoredText;
            var snapshot = new LayoutTargetSnapshot(
                objectId,
                target.Manifest,
                correctionText,
                IsChanged: true,
                entity.OwnerId,
                Bounds2d.From(topology.Source.Bounds));
            LayoutAdjustment? correction = TableCellLayout.Apply(
                database,
                transaction,
                snapshot,
                topology,
                allowed,
                reasonPrefix: "global-collision",
                force: true,
                absoluteMinimumHeight:
                    LayoutCorrectionPolicy.MinimumReadableHeight(
                        topology.Source.OriginalTextHeight));
            if (correction is not null)
            {
                corrections.Add(correction);
            }
            else
            {
                throw new CommandProtocolException("correction_not_applied", $"Global correction could not edit {recordId} at {auditText.NewHandle}.");
            }
        }

        return corrections.ToArray();
    }

    private static void MoveToSourceAnchor(
        Entity entity,
        CadLayoutText topology,
        GlobalCorrectionTarget target)
    {
        if (entity is MText mText)
        {
            mText.Attachment = TextAnchorMapper.ToAttachment(
                target.Manifest.Properties.HorizontalMode,
                target.Manifest.Properties.VerticalMode);
            mText.Location = new Autodesk.AutoCAD.Geometry.Point3d(
                topology.Source.Anchor.X,
                topology.Source.Anchor.Y,
                mText.Location.Z);
            mText.TextHeight = LayoutCorrectionPolicy.RestoreSourceTextHeight(
                mText.TextHeight,
                topology.Source.OriginalTextHeight);
            return;
        }

        if (entity is DBText dbText)
        {
            dbText.Height = LayoutCorrectionPolicy.RestoreSourceTextHeight(
                dbText.Height,
                topology.Source.OriginalTextHeight);
            Autodesk.AutoCAD.Geometry.Point3d current =
                dbText.HorizontalMode == TextHorizontalMode.TextLeft &&
                dbText.VerticalMode == TextVerticalMode.TextBase
                    ? dbText.Position
                    : dbText.AlignmentPoint;
            dbText.TransformBy(Autodesk.AutoCAD.Geometry.Matrix3d.Displacement(
                new Autodesk.AutoCAD.Geometry.Vector3d(
                    topology.Source.Anchor.X - current.X,
                    topology.Source.Anchor.Y - current.Y,
                    0)));
        }
    }

    private static LayoutTargetSnapshot CreateSnapshot(
        Database database,
        Transaction transaction,
        CadLayoutText topology,
        IReadOnlyDictionary<string, LayoutAuditTextRow> auditByRecord,
        IReadOnlyDictionary<string, GlobalCorrectionTarget> targets)
    {
        if (!auditByRecord.TryGetValue(topology.RecordId, out LayoutAuditTextRow? auditText))
            throw new CommandProtocolException("correction_audit_text_missing", $"No audit text for {topology.RecordId}.");
        if (!targets.TryGetValue(topology.RecordId, out GlobalCorrectionTarget? target))
            throw new CommandProtocolException("correction_target_missing", $"No translated target for {topology.RecordId}.");
        ObjectId objectId = ResolveObjectId(database, auditText.NewHandle);
        Entity entity = (Entity)transaction.GetObject(objectId, OpenMode.ForRead, false);
        string correctionText = entity is MText currentMText
            ? LayoutTextNormalization.RemoveGeneratedWidthWrapper(currentMText.Contents)
            : target.RestoredText;
        return new LayoutTargetSnapshot(
            objectId,
            target.Manifest,
            correctionText,
            IsChanged: true,
            entity.OwnerId,
            Bounds2d.From(topology.Source.Bounds));
    }

    private static Dictionary<string, Rect2> AllocateCorrectionSlots(CadLayoutBaseline baseline)
    {
        var result = new Dictionary<string, Rect2>(StringComparer.Ordinal);
        foreach (CadDefinitionTopology definition in baseline.Definitions)
        {
            IReadOnlyDictionary<string, Rect2> slots = SourceNeighborSlotAllocator.Allocate(
                definition.Texts.Select(text => new LayoutTextBoxSample(
                    text.RecordId,
                    text.Source.Bounds,
                    text.Source.OriginalTextHeight)).ToArray());
            foreach ((string recordId, Rect2 slot) in slots)
            {
                result[recordId] = slot;
            }

            foreach (IGrouping<string, CadLayoutText> tableCell in definition.Texts
                         .Where(text => text.Region?.Kind == LayoutRegionKind.TableCell)
                         .GroupBy(text => text.Region!.Id, StringComparer.Ordinal))
            {
                CadLayoutText[] siblings = tableCell.ToArray();
                IReadOnlyDictionary<string, Rect2> exclusive = ExclusiveTextBoxAllocator.Allocate(
                    siblings[0].Region!.Bounds,
                    siblings.Select(text => new LayoutTextBoxSample(
                        text.RecordId,
                        text.Source.Bounds,
                        text.Source.OriginalTextHeight)).ToArray());
                foreach ((string recordId, Rect2 slot) in exclusive)
                {
                    result[recordId] = slot;
                }
            }
        }

        return result;
    }

    private static ObjectId ResolveObjectId(Database database, string handle)
    {
        if (!long.TryParse(handle, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long value))
        {
            throw new CommandProtocolException("invalid_handle", $"Correction handle {handle} is invalid.");
        }

        ObjectId objectId = database.GetObjectId(false, new Handle(value), 0);
        if (objectId.IsNull)
        {
            throw new CommandProtocolException("missing_handle", $"Correction handle {handle} no longer exists.");
        }

        return objectId;
    }

    private static LayoutRiskCode ParseCode(string value) => value switch
    {
        "text-overlap" => LayoutRiskCode.TextOverlap,
        "geometry-overlap" => LayoutRiskCode.GeometryOverlap,
        "cross-region" => LayoutRiskCode.CrossRegion,
        "anchor-drift" => LayoutRiskCode.AnchorDrift,
        _ => LayoutRiskCode.Overflow
    };

    private static LayoutRiskLevel ParseLevel(string value) => value switch
    {
        "high" => LayoutRiskLevel.High,
        "medium" => LayoutRiskLevel.Medium,
        _ => LayoutRiskLevel.Low
    };
}

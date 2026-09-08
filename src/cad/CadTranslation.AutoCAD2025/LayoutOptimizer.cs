using Autodesk.AutoCAD.DatabaseServices;
using CadTranslation.Contracts;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

internal sealed record LayoutWriteInput(
    ObjectId ObjectId,
    ManifestRecord Manifest,
    string RestoredText,
    bool IsChanged);

internal sealed record LayoutTargetSnapshot(
    ObjectId ObjectId,
    ManifestRecord Manifest,
    string RestoredText,
    bool IsChanged,
    ObjectId OwnerId,
    Bounds2d? SourceBounds);

internal sealed record LayoutAdjustment(
    string RecordId,
    string OldHandle,
    string NewHandle,
    string ObjectType,
    IReadOnlyList<string> Actions,
    double OriginalHeight,
    double NewHeight,
    double OriginalWidthFactor,
    double NewWidthFactor,
    Bounds2d? SourceBounds,
    Bounds2d? CandidateBounds,
    bool ManualReview,
    string Reason);

internal sealed record LayoutOptimizationResult(
    int TotalChanged,
    int Unchanged,
    int Wrapped,
    int WidthCompressed,
    int HeightReduced,
    int Reflowed,
    int ManualReview,
    int RemainingOverflow,
    IReadOnlyList<string> UncoveredRecordIds,
    IReadOnlyList<LayoutAdjustment> Adjustments);

internal sealed record Bounds2d(double MinX, double MinY, double MaxX, double MaxY)
{
    internal double Width => Math.Max(0, MaxX - MinX);
    internal double Height => Math.Max(0, MaxY - MinY);

    internal bool Contains(Bounds2d value, double tolerance = 1e-6) =>
        value.MinX >= MinX - tolerance &&
        value.MaxX <= MaxX + tolerance &&
        value.MinY >= MinY - tolerance &&
        value.MaxY <= MaxY + tolerance;

    internal static Bounds2d From(Rect2 value) =>
        new(value.Left, value.Bottom, value.Right, value.Top);
}

internal static class LayoutOptimizer
{
    internal static LayoutOptimizationResult MergeCorrections(
        LayoutOptimizationResult source,
        IReadOnlyList<LayoutAdjustment> corrections)
    {
        if (corrections.Count == 0)
        {
            return source;
        }

        Dictionary<string, LayoutAdjustment> correctionByRecord = corrections
            .GroupBy(correction => correction.RecordId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var merged = new List<LayoutAdjustment>(source.Adjustments.Count);
        foreach (LayoutAdjustment adjustment in source.Adjustments)
        {
            if (!correctionByRecord.Remove(adjustment.RecordId, out LayoutAdjustment? correction))
            {
                merged.Add(adjustment);
                continue;
            }

            merged.Add(adjustment with
            {
                NewHandle = correction.NewHandle,
                Actions = adjustment.Actions
                    .Concat(correction.Actions)
                    .Append("global-collision-correction")
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                NewHeight = correction.NewHeight,
                NewWidthFactor = correction.NewWidthFactor,
                CandidateBounds = correction.CandidateBounds,
                ManualReview = correction.ManualReview,
                Reason = correction.Reason
            });
        }

        merged.AddRange(correctionByRecord.Values);
        LayoutAdjustment[] values = merged.ToArray();
        return source with
        {
            Unchanged = Math.Max(0, source.TotalChanged - values.Length),
            Wrapped = values.Count(adjustment => adjustment.Actions.Contains("wrap", StringComparer.Ordinal)),
            WidthCompressed = values.Count(adjustment => adjustment.Actions.Contains("compress-width", StringComparer.Ordinal)),
            HeightReduced = values.Count(adjustment => adjustment.Actions.Contains("shrink-height", StringComparer.Ordinal)),
            Reflowed = values.Count(adjustment => adjustment.Actions.Contains("reflow", StringComparer.Ordinal)),
            ManualReview = values.Count(adjustment => adjustment.ManualReview),
            RemainingOverflow = values.Count(adjustment =>
                adjustment.SourceBounds is not null &&
                adjustment.CandidateBounds is not null &&
                !adjustment.SourceBounds.Contains(adjustment.CandidateBounds)),
            Adjustments = values
        };
    }

    internal static LayoutTargetSnapshot[] Capture(
        Transaction transaction,
        IReadOnlyList<LayoutWriteInput> inputs)
    {
        var snapshots = new List<LayoutTargetSnapshot>(inputs.Count);
        foreach (LayoutWriteInput input in inputs)
        {
            DBObject value = transaction.GetObject(input.ObjectId, OpenMode.ForRead, false);
            Bounds2d? sourceBounds = value is Entity entity
                ? CadLayoutGeometry.TryLayoutBounds(entity)
                : null;
            snapshots.Add(new LayoutTargetSnapshot(
                input.ObjectId,
                input.Manifest,
                input.RestoredText,
                input.IsChanged,
                value.OwnerId,
                sourceBounds));
        }

        return snapshots.ToArray();
    }

    internal static LayoutOptimizationResult Optimize(
        Database database,
        Transaction transaction,
        IReadOnlyList<LayoutTargetSnapshot> snapshots,
        CadLayoutBaseline baseline)
    {
        IReadOnlyDictionary<ObjectId, CadLayoutText> topology = baseline.TextByObjectId;
        IReadOnlyDictionary<ObjectId, Rect2> tableTextBoxes =
            AllocateExclusiveTableTextBoxes(topology.Values);
        IReadOnlyDictionary<ObjectId, Rect2> fixedTextBoxes =
            AllocateSourceNeighborTextBoxes(topology.Values);
        var adjustments = new List<LayoutAdjustment>();
        var consumed = new HashSet<ObjectId>();

        foreach (LayoutTargetSnapshot target in snapshots.Where(target => target.IsChanged))
        {
            if (!topology.TryGetValue(target.ObjectId, out CadLayoutText? text) ||
                LayoutRouter.Select(text.Region?.Kind, text.CandidateText) != LayoutHandlerKind.TableCell)
            {
                continue;
            }

            LayoutAdjustment? adjustment = TableCellLayout.Apply(
                database,
                transaction,
                target,
                text,
                tableTextBoxes.GetValueOrDefault(target.ObjectId, text.Region!.Bounds));
            consumed.Add(target.ObjectId);
            if (adjustment is not null)
            {
                adjustments.Add(adjustment);
            }
        }

        LayoutTargetSnapshot[] narrativeCandidates = snapshots
            .Where(target => !consumed.Contains(target.ObjectId))
            .Where(target => topology.TryGetValue(target.ObjectId, out CadLayoutText? text) &&
                             LayoutRouter.Select(text.Region?.Kind, text.CandidateText) == LayoutHandlerKind.NoteColumn)
            .ToArray();
        string[] narrativeParticipantIds = LayoutGroupParticipation.SelectNarrativeParticipants(
            narrativeCandidates.Select(target => new LayoutParticipationItem(
                target.Manifest.RecordId,
                topology[target.ObjectId].Region!.Id,
                target.IsChanged)).ToArray());
        var narrativeParticipantSet = new HashSet<string>(
            narrativeParticipantIds,
            StringComparer.Ordinal);
        foreach (IGrouping<string, LayoutTargetSnapshot> group in narrativeCandidates
                     .Where(target => narrativeParticipantSet.Contains(target.Manifest.RecordId))
                     .GroupBy(target => topology[target.ObjectId].Region!.Id, StringComparer.Ordinal))
        {
            LayoutTargetSnapshot[] targets = group.ToArray();
            adjustments.AddRange(NoteColumnLayout.Apply(
                database,
                transaction,
                targets,
                targets.Select(target => topology[target.ObjectId]).ToArray()));
            foreach (LayoutTargetSnapshot target in targets)
            {
                consumed.Add(target.ObjectId);
            }
        }

        foreach (LayoutTargetSnapshot target in snapshots
                     .Where(target => target.IsChanged && !consumed.Contains(target.ObjectId)))
        {
            if (!topology.TryGetValue(target.ObjectId, out CadLayoutText? text) ||
                !fixedTextBoxes.TryGetValue(target.ObjectId, out Rect2 allowed))
            {
                continue;
            }

            if (text.Region?.Kind == LayoutRegionKind.ClosedFrame)
            {
                allowed = IntersectOrFallback(
                    allowed,
                    text.Region.Bounds,
                    text.Source.Bounds);
            }

            LayoutAdjustment? adjustment = FixedLabelLayout.Apply(
                database,
                transaction,
                target,
                text,
                allowed);
            consumed.Add(target.ObjectId);
            if (adjustment is not null)
            {
                adjustments.Add(adjustment);
            }
        }

        int totalChanged = snapshots.Count(target => target.IsChanged);
        string[] changedRecordIds = snapshots
            .Where(target => target.IsChanged)
            .Select(target => target.Manifest.RecordId)
            .ToArray();
        string[] handledRecordIds = snapshots
            .Where(target => target.IsChanged && consumed.Contains(target.ObjectId))
            .Select(target => target.Manifest.RecordId)
            .ToArray();
        string[] uncoveredRecordIds = LayoutCoveragePolicy.FindUncovered(
            changedRecordIds,
            handledRecordIds);
        return new LayoutOptimizationResult(
            totalChanged,
            Math.Max(0, totalChanged - adjustments.Count),
            adjustments.Count(adjustment => adjustment.Actions.Contains("wrap", StringComparer.Ordinal)),
            adjustments.Count(adjustment => adjustment.Actions.Contains("compress-width", StringComparer.Ordinal)),
            adjustments.Count(adjustment => adjustment.Actions.Contains("shrink-height", StringComparer.Ordinal)),
            adjustments.Count(adjustment => adjustment.Actions.Contains("reflow", StringComparer.Ordinal)),
            adjustments.Count(adjustment => adjustment.ManualReview),
            adjustments.Count(adjustment =>
                adjustment.SourceBounds is not null &&
                adjustment.CandidateBounds is not null &&
                !adjustment.SourceBounds.Contains(adjustment.CandidateBounds)),
            uncoveredRecordIds,
            adjustments);
    }

    private static IReadOnlyDictionary<ObjectId, Rect2> AllocateExclusiveTableTextBoxes(
        IEnumerable<CadLayoutText> texts)
    {
        var result = new Dictionary<ObjectId, Rect2>();
        foreach (IGrouping<string, CadLayoutText> group in texts
                     .Where(text => text.Region?.Kind == LayoutRegionKind.TableCell)
                     .GroupBy(
                         text => $"{text.DefinitionName}\u001f{text.Region!.Id}",
                         StringComparer.Ordinal))
        {
            CadLayoutText[] siblings = group.ToArray();
            Rect2 parent = siblings[0].Region!.Bounds;
            IReadOnlyDictionary<string, Rect2> allocated =
                ExclusiveTextBoxAllocator.Allocate(
                    parent,
                    siblings.Select(text => new LayoutTextBoxSample(
                        text.EntityHandle,
                        text.Source.Bounds,
                        text.Source.OriginalTextHeight)).ToArray());
            foreach (CadLayoutText text in siblings)
            {
                result[text.ObjectId] = allocated[text.EntityHandle];
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<ObjectId, Rect2> AllocateSourceNeighborTextBoxes(
        IEnumerable<CadLayoutText> texts)
    {
        var result = new Dictionary<ObjectId, Rect2>();
        foreach (IGrouping<string, CadLayoutText> definition in texts
                     .GroupBy(text => text.DefinitionName, StringComparer.Ordinal))
        {
            CadLayoutText[] items = definition.ToArray();
            IReadOnlyDictionary<string, Rect2> allocated =
                SourceNeighborSlotAllocator.Allocate(
                    items.Select(text => new LayoutTextBoxSample(
                        text.EntityHandle,
                        text.Source.Bounds,
                        text.Source.OriginalTextHeight)).ToArray());
            foreach (CadLayoutText text in items)
            {
                result[text.ObjectId] = allocated[text.EntityHandle];
            }
        }

        return result;
    }

    private static Rect2 IntersectOrFallback(
        Rect2 candidate,
        Rect2 container,
        Rect2 source)
    {
        double left = Math.Max(candidate.Left, container.Left);
        double bottom = Math.Max(candidate.Bottom, container.Bottom);
        double right = Math.Min(candidate.Right, container.Right);
        double top = Math.Min(candidate.Top, container.Top);
        if (right <= left || top <= bottom)
        {
            return container;
        }

        return new Rect2(
            Math.Min(left, source.Left),
            Math.Min(bottom, source.Bottom),
            Math.Max(right, source.Right),
            Math.Max(top, source.Top));
    }
}

using Autodesk.AutoCAD.DatabaseServices;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

internal static class LayoutOptimizerV2
{
    internal static LayoutOptimizationResult Optimize(
        Database database,
        Transaction transaction,
        IReadOnlyList<LayoutTargetSnapshot> snapshots,
        CadLayoutBaseline baseline)
    {
        IReadOnlyDictionary<ObjectId, CadLayoutText> topology = baseline.TextByObjectId;
        IReadOnlyDictionary<ObjectId, Rect2> fixedSlots = AllocateFixedSlots(topology.Values);
        HashSet<string> narrativeParticipants = SelectNarrativeParticipants(snapshots, topology);
        LayoutTargetSnapshot[] participants = snapshots
            .Where(target => target.IsChanged || narrativeParticipants.Contains(target.Manifest.RecordId))
            .Where(target => topology.ContainsKey(target.ObjectId))
            .ToArray();
        LayoutV2Input[] inputs = participants
            .Select(target => CreateInput(
                target,
                topology[target.ObjectId],
                baseline,
                fixedSlots))
            .ToArray();
        IReadOnlyDictionary<string, LayoutV2Decision> decisions = LayoutV2Planner.Plan(inputs)
            .ToDictionary(decision => decision.RecordId, StringComparer.Ordinal);

        var adjustments = new List<LayoutAdjustment>();
        var consumed = new HashSet<ObjectId>();

        foreach (LayoutTargetSnapshot target in participants.Where(target => target.IsChanged))
        {
            CadLayoutText text = topology[target.ObjectId];
            LayoutV2Decision decision = decisions[target.Manifest.RecordId];
            if (decision.Kind != LayoutV2Kind.TableCell)
            {
                continue;
            }

            LayoutAdjustment? adjustment = TableCellLayout.Apply(
                database,
                transaction,
                target,
                text,
                decision.AllowedBounds,
                "layout-v2-table");
            AddDecision(adjustments, adjustment, target, text, decision);
            consumed.Add(target.ObjectId);
        }

        foreach (IGrouping<string, LayoutTargetSnapshot> group in participants
                     .Where(target => decisions[target.Manifest.RecordId].Kind == LayoutV2Kind.Narrative)
                     .GroupBy(target => GroupId(topology[target.ObjectId]), StringComparer.Ordinal))
        {
            LayoutTargetSnapshot[] targets = group.ToArray();
            CadLayoutText[] texts = targets.Select(target => topology[target.ObjectId]).ToArray();
            LayoutV2Decision decision = decisions[targets[0].Manifest.RecordId];
            LayoutAdjustment[] groupAdjustments = NoteColumnLayout.Apply(
                database,
                transaction,
                targets,
                texts,
                LayoutFitPolicy.MinimumHeightScale,
                decision.AllowedBounds);
            foreach (LayoutAdjustment adjustment in groupAdjustments)
            {
                LayoutV2Decision itemDecision = decisions[adjustment.RecordId];
                adjustments.Add(itemDecision.ManualReview
                    ? adjustment with { ManualReview = true, Reason = itemDecision.Reason }
                    : adjustment with { Reason = "layout-v2-narrative" });
            }
            foreach (LayoutTargetSnapshot target in targets)
            {
                consumed.Add(target.ObjectId);
            }
        }

        foreach (LayoutTargetSnapshot target in participants
                     .Where(target => target.IsChanged && !consumed.Contains(target.ObjectId)))
        {
            CadLayoutText text = topology[target.ObjectId];
            LayoutV2Decision decision = decisions[target.Manifest.RecordId];
            LayoutAdjustment? adjustment = FixedLabelLayout.Apply(
                database,
                transaction,
                target,
                text,
                decision.AllowedBounds);
            AddDecision(adjustments, adjustment, target, text, decision);
            consumed.Add(target.ObjectId);
        }

        string[] uncovered = snapshots
            .Where(target => target.IsChanged && !consumed.Contains(target.ObjectId))
            .Select(target => target.Manifest.RecordId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        int totalChanged = snapshots.Count(target => target.IsChanged);
        return Summarize(totalChanged, adjustments, uncovered);
    }

    private static LayoutV2Input CreateInput(
        LayoutTargetSnapshot target,
        CadLayoutText text,
        CadLayoutBaseline baseline,
        IReadOnlyDictionary<ObjectId, Rect2> fixedSlots)
    {
        CadDefinitionTopology definition = baseline.Definitions.First(item =>
            string.Equals(item.Name, text.DefinitionName, StringComparison.Ordinal));
        LayoutV2Kind kind = Kind(text);
        Rect2 parent = kind switch
        {
            LayoutV2Kind.TableCell or LayoutV2Kind.Narrative when text.Region is not null =>
                text.Region.Bounds,
            _ => fixedSlots.GetValueOrDefault(text.ObjectId, text.Source.Bounds)
        };
        if (kind == LayoutV2Kind.FixedLabel)
        {
            LayoutRegion? frame = definition.Regions
                .Where(region => region.Kind == LayoutRegionKind.ClosedFrame)
                .Where(region => region.Bounds.Contains(text.Source.Bounds))
                .OrderBy(region => region.Bounds.Area)
                .FirstOrDefault();
            if (frame is not null)
            {
                parent = IntersectOrSource(parent, frame.Bounds, text.Source.Bounds);
            }
        }

        Rect2[] hardKeepouts = kind == LayoutV2Kind.Narrative
            ? definition.Regions
                .Where(region => region.Kind is LayoutRegionKind.TableCell or LayoutRegionKind.TitleBlock)
                .Where(region => text.Region is null || !string.Equals(region.Id, text.Region.Id, StringComparison.Ordinal))
                .Select(region => region.Bounds)
                .Concat(definition.ProtectedGeometry.Select(item => item.Bounds))
                .Distinct()
                .ToArray()
            : [];
        string groupId = kind == LayoutV2Kind.FixedLabel
            ? target.Manifest.RecordId
            : GroupId(text);
        return new LayoutV2Input(
            target.Manifest.RecordId,
            groupId,
            kind,
            text.Source.Bounds,
            parent,
            text.Source.OriginalTextHeight,
            hardKeepouts);
    }

    private static HashSet<string> SelectNarrativeParticipants(
        IReadOnlyList<LayoutTargetSnapshot> snapshots,
        IReadOnlyDictionary<ObjectId, CadLayoutText> topology)
    {
        LayoutTargetSnapshot[] candidates = snapshots
            .Where(target => topology.TryGetValue(target.ObjectId, out CadLayoutText? text) &&
                             Kind(text) == LayoutV2Kind.Narrative)
            .ToArray();
        string[] ids = LayoutGroupParticipation.SelectNarrativeParticipants(
            candidates.Select(target => new LayoutParticipationItem(
                target.Manifest.RecordId,
                GroupId(topology[target.ObjectId]),
                target.IsChanged)).ToArray());
        return new HashSet<string>(ids, StringComparer.Ordinal);
    }

    private static LayoutV2Kind Kind(CadLayoutText text) => text.Region?.Kind switch
    {
        LayoutRegionKind.TableCell => LayoutV2Kind.TableCell,
        LayoutRegionKind.NoteColumn => LayoutV2Kind.Narrative,
        _ => LayoutV2Kind.FixedLabel
    };

    private static string GroupId(CadLayoutText text) =>
        $"{text.DefinitionName}\u001f{text.Region?.Id ?? text.EntityHandle}";

    private static IReadOnlyDictionary<ObjectId, Rect2> AllocateFixedSlots(
        IEnumerable<CadLayoutText> texts)
    {
        var result = new Dictionary<ObjectId, Rect2>();
        foreach (IGrouping<string, CadLayoutText> definition in texts
                     .GroupBy(text => text.DefinitionName, StringComparer.Ordinal))
        {
            CadLayoutText[] items = definition.ToArray();
            IReadOnlyDictionary<string, Rect2> slots = SourceNeighborSlotAllocator.Allocate(
                items.Select(text => new LayoutTextBoxSample(
                    text.EntityHandle,
                    text.Source.Bounds,
                    text.Source.OriginalTextHeight)).ToArray());
            foreach (CadLayoutText text in items)
            {
                result[text.ObjectId] = slots[text.EntityHandle];
            }
        }
        return result;
    }

    private static Rect2 IntersectOrSource(Rect2 first, Rect2 second, Rect2 source)
    {
        double left = Math.Max(first.Left, second.Left);
        double bottom = Math.Max(first.Bottom, second.Bottom);
        double right = Math.Min(first.Right, second.Right);
        double top = Math.Min(first.Top, second.Top);
        if (right <= left || top <= bottom)
        {
            return source;
        }
        return new Rect2(
            Math.Min(left, source.Left),
            Math.Min(bottom, source.Bottom),
            Math.Max(right, source.Right),
            Math.Max(top, source.Top));
    }

    private static void AddDecision(
        ICollection<LayoutAdjustment> adjustments,
        LayoutAdjustment? adjustment,
        LayoutTargetSnapshot target,
        CadLayoutText text,
        LayoutV2Decision decision)
    {
        if (adjustment is not null)
        {
            adjustments.Add(decision.ManualReview
                ? adjustment with { ManualReview = true, Reason = decision.Reason }
                : adjustment);
            return;
        }
        if (!decision.ManualReview)
        {
            return;
        }

        adjustments.Add(new LayoutAdjustment(
            target.Manifest.RecordId,
            text.EntityHandle,
            text.EntityHandle,
            text.ObjectType,
            [],
            text.Source.OriginalTextHeight,
            text.Source.OriginalTextHeight,
            1,
            1,
            target.SourceBounds,
            target.SourceBounds,
            true,
            decision.Reason));
    }

    private static LayoutOptimizationResult Summarize(
        int totalChanged,
        IReadOnlyList<LayoutAdjustment> adjustments,
        IReadOnlyList<string> uncovered) => new(
        totalChanged,
        Math.Max(0, totalChanged - adjustments.Count),
        adjustments.Count(item => item.Actions.Contains("wrap", StringComparer.Ordinal)),
        adjustments.Count(item => item.Actions.Contains("compress-width", StringComparer.Ordinal)),
        adjustments.Count(item => item.Actions.Contains("shrink-height", StringComparer.Ordinal)),
        adjustments.Count(item => item.Actions.Contains("reflow", StringComparer.Ordinal)),
        adjustments.Count(item => item.ManualReview),
        adjustments.Count(item => item.SourceBounds is not null &&
                                  item.CandidateBounds is not null &&
                                  !item.SourceBounds.Contains(item.CandidateBounds)),
        uncovered,
        adjustments);
}

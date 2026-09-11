using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

internal static class NoteColumnLayout
{
    private sealed record WorkingText(
        LayoutTargetSnapshot Target,
        CadLayoutText Topology,
        DBText? Source,
        MText Value,
        double OriginalHeight,
        double OriginalWidthFactor,
        string OldHandle);

    private sealed record WorkingCluster(
        Rect2 SourceBounds,
        IReadOnlyList<WorkingText> Items);

    private sealed record WorkingRow(
        Rect2 SourceBounds,
        IReadOnlyList<WorkingCluster> Clusters)
    {
        internal IReadOnlyList<WorkingText> Items =>
            Clusters.SelectMany(cluster => cluster.Items).ToArray();
    }

    private sealed record PackedWorkingCluster(
        double StartX,
        NarrativeInlinePacking Packing);

    private sealed record PackedWorkingRow(
        IReadOnlyList<PackedWorkingCluster> Clusters,
        double Height);

    internal static LayoutAdjustment[] Apply(
        Database database,
        Transaction transaction,
        IReadOnlyList<LayoutTargetSnapshot> targets,
        IReadOnlyList<CadLayoutText> topology,
        double minimumHeightScale = LayoutFitPolicy.EmergencyMinimumHeightScale,
        Rect2? allowedOverride = null)
    {
        if (targets.Count == 0 ||
            topology.Count != targets.Count ||
            (topology[0].Region is null && allowedOverride is null))
        {
            return [];
        }

        var working = new List<WorkingText>();
        for (int index = 0; index < targets.Count; index++)
        {
            LayoutTargetSnapshot target = targets[index];
            CadLayoutText textTopology = topology[index];
            DBObject value = transaction.GetObject(target.ObjectId, OpenMode.ForWrite, false);
            if (value is AttributeDefinition or AttributeReference)
            {
                continue;
            }

            if (value is DBText dbText)
            {
                Rect2 textRegion = allowedOverride ?? textTopology.Region!.Bounds;
                double width = Math.Max(
                    dbText.Height,
                    textRegion.Right - textTopology.Source.Bounds.Left);
                MText replacement = TextAnchorMapper.Replace(
                    database,
                    transaction,
                    dbText,
                    target.RestoredText,
                    AttachmentPoint.TopLeft,
                    new Point2(textTopology.Source.Bounds.Left, textTopology.Source.Bounds.Top),
                    width);
                working.Add(new WorkingText(
                    target,
                    textTopology,
                    dbText,
                    replacement,
                    dbText.Height,
                    dbText.WidthFactor,
                    dbText.Handle.ToString()));
            }
            else if (value is MText mText)
            {
                working.Add(new WorkingText(
                    target,
                    textTopology,
                    null,
                    mText,
                    mText.TextHeight,
                    1,
                    mText.Handle.ToString()));
            }
        }

        if (working.Count == 0)
        {
            return [];
        }

        WorkingText[] ordered = working
            .OrderByDescending(item => item.Topology.Source.Bounds.Top)
            .ThenBy(item => item.Topology.Source.Bounds.Left)
            .ToArray();
        Rect2 region = allowedOverride ?? ordered[0].Topology.Region!.Bounds;
        double medianHeight = Median(ordered.Select(item => item.OriginalHeight));
        WorkingRow[] rows = BuildRows(ordered, medianHeight);
        Rect2 inner = CadLayoutGeometry.Inset(region, medianHeight * 0.10);
        double startTop = Math.Min(inner.Top, ordered.Max(item => item.Topology.Source.Bounds.Top));
        double availableHeight = Math.Max(medianHeight, startTop - inner.Bottom);

        (double widthScale, double heightScale, bool fits) = SelectScale(
            rows,
            inner,
            availableHeight,
            medianHeight,
            minimumHeightScale);
        Position(rows, inner, startTop, widthScale, heightScale, medianHeight);
        bool allItemsInside = AllInside(working, inner);
        if (LayoutFitPolicy.NeedsContainmentRetry(fits, allItemsInside))
        {
            fits = false;
            widthScale = LayoutFitPolicy.MinimumWidthScale;
            foreach (double retryScale in CadLayoutGeometry.Steps(
                         heightScale,
                         minimumHeightScale,
                         0.05))
            {
                Position(
                    rows,
                    inner,
                    startTop,
                    widthScale,
                    retryScale,
                    medianHeight);
                heightScale = retryScale;
                if (AllInside(working, inner))
                {
                    fits = true;
                    break;
                }
            }
        }

        var adjustments = new List<LayoutAdjustment>(ordered.Length);
        foreach (WorkingText item in ordered)
        {
            Bounds2d? candidate = CadLayoutGeometry.TryFreshBounds(item.Value);
            bool inside = candidate is not null && Bounds2d.From(inner).Contains(candidate);
            var actions = new List<string> { "wrap", "reflow" };
            if (widthScale < 0.999) actions.Add("compress-width");
            if (heightScale < 0.999) actions.Add("shrink-height");
            adjustments.Add(new LayoutAdjustment(
                item.Target.Manifest.RecordId,
                item.OldHandle,
                item.Value.Handle.ToString(),
                "AcDbMText",
                actions,
                item.OriginalHeight,
                item.Value.TextHeight,
                item.OriginalWidthFactor,
                widthScale,
                item.Target.SourceBounds,
                candidate,
                !fits || !inside,
                fits && inside ? "note-column-reflow" : "note-column-readable-floor"));
        }

        foreach (WorkingText item in ordered.Where(item => item.Source is not null))
        {
            item.Source!.Erase();
        }

        return adjustments.ToArray();
    }

    private static bool AllInside(
        IEnumerable<WorkingText> items,
        Rect2 inner)
    {
        Bounds2d allowed = Bounds2d.From(inner);
        return items.All(item =>
            CadLayoutGeometry.TryFreshBounds(item.Value) is Bounds2d bounds &&
            allowed.Contains(bounds));
    }

    private static (double WidthScale, double HeightScale, bool Fits) SelectScale(
        IReadOnlyList<WorkingRow> rows,
        Rect2 inner,
        double availableHeight,
        double medianHeight,
        double minimumHeightScale)
    {
        foreach (double widthScale in CadLayoutGeometry.Steps(1, LayoutFitPolicy.MinimumWidthScale, 0.05))
        {
            Prepare(rows, inner, widthScale, 1);
            if (RequiredHeight(rows, inner, widthScale, medianHeight) <= availableHeight)
            {
                return (widthScale, 1, true);
            }
        }

        foreach (double heightScale in CadLayoutGeometry.Steps(
                     0.95,
                     minimumHeightScale,
                     0.05))
        {
            Prepare(rows, inner, LayoutFitPolicy.MinimumWidthScale, heightScale);
            if (RequiredHeight(
                    rows,
                    inner,
                    LayoutFitPolicy.MinimumWidthScale,
                    medianHeight * heightScale) <= availableHeight)
            {
                return (LayoutFitPolicy.MinimumWidthScale, heightScale, true);
            }
        }

        return (
            LayoutFitPolicy.MinimumWidthScale,
            minimumHeightScale,
            false);
    }

    private static void Prepare(
        IEnumerable<WorkingRow> rows,
        Rect2 inner,
        double widthScale,
        double heightScale)
    {
        foreach (WorkingRow row in rows)
        {
            foreach (WorkingText item in row.Items)
            {
                string contents = item.Source is null
                    ? item.Target.RestoredText
                    : NarrativeTargetFontPolicy.ApplyLatinWrapper(item.Target.RestoredText);
                item.Value.Contents = CadLayoutGeometry.FormatWidth(contents, widthScale);
                item.Value.TextHeight = item.OriginalHeight * heightScale;
            }

            PackRow(row, inner, widthScale);
        }
    }

    private static double RequiredHeight(
        IReadOnlyList<WorkingRow> rows,
        Rect2 inner,
        double widthScale,
        double scaledMedianHeight)
    {
        double rowHeight = rows.Sum(row => PackRow(row, inner, widthScale).Height);
        double gapHeight = Math.Max(0, rows.Count - 1) * scaledMedianHeight * 0.20;
        return rowHeight + gapHeight;
    }

    private static void Position(
        IReadOnlyList<WorkingRow> rows,
        Rect2 inner,
        double startTop,
        double widthScale,
        double heightScale,
        double medianHeight)
    {
        Prepare(rows, inner, widthScale, heightScale);
        double cursor = startTop;
        WorkingRow? previous = null;
        foreach (WorkingRow row in rows)
        {
            if (previous is not null)
            {
                double sourceGap = previous.SourceBounds.Bottom - row.SourceBounds.Top;
                cursor -= Math.Clamp(
                    sourceGap,
                    medianHeight * heightScale * 0.20,
                    medianHeight * heightScale * 1.50);
            }

            PackedWorkingRow packed = PackRow(row, inner, widthScale);
            for (int clusterIndex = 0; clusterIndex < row.Clusters.Count; clusterIndex++)
            {
                WorkingCluster cluster = row.Clusters[clusterIndex];
                PackedWorkingCluster packedCluster = packed.Clusters[clusterIndex];
                IReadOnlyDictionary<string, NarrativeInlinePlacement> placementById =
                    packedCluster.Packing.Placements.ToDictionary(
                        placement => placement.Id,
                        StringComparer.Ordinal);
                foreach (WorkingText item in cluster.Items)
                {
                    NarrativeInlinePlacement placement =
                        placementById[item.Topology.EntityHandle];
                    item.Value.Attachment = AttachmentPoint.TopLeft;
                    item.Value.Location = new Point3d(
                        packedCluster.StartX + placement.XOffset,
                        cursor - placement.YOffset,
                        item.Value.Location.Z);
                }
            }

            cursor -= packed.Height;
            previous = row;
        }
    }

    private static PackedWorkingRow PackRow(
        WorkingRow row,
        Rect2 inner,
        double widthScale)
    {
        var packed = new List<PackedWorkingCluster>(row.Clusters.Count);
        for (int clusterIndex = 0; clusterIndex < row.Clusters.Count; clusterIndex++)
        {
            WorkingCluster cluster = row.Clusters[clusterIndex];
            double minimumHeight = cluster.Items.Min(item => item.Value.TextHeight);
            double maximumStartX = Math.Max(inner.Left, inner.Right - minimumHeight);
            double startX = Math.Clamp(
                cluster.SourceBounds.Left,
                inner.Left,
                maximumStartX);
            double slotRight = clusterIndex + 1 < row.Clusters.Count
                ? Midpoint(
                    cluster.SourceBounds.Right,
                    row.Clusters[clusterIndex + 1].SourceBounds.Left)
                : inner.Right;
            slotRight = Math.Clamp(slotRight, startX + minimumHeight, inner.Right);
            double availableWidth = Math.Max(minimumHeight, slotRight - startX);
            var inlineItems = new List<NarrativeInlineItem>(cluster.Items.Count);
            foreach (WorkingText item in cluster.Items)
            {
                double desiredWidth = Math.Clamp(
                    LayoutTextMetrics.EstimateEmWidth(item.Target.RestoredText) *
                    item.Value.TextHeight *
                    widthScale *
                    1.05,
                    item.Value.TextHeight,
                    availableWidth);
                item.Value.Width = desiredWidth;
                double renderedWidth = CadLayoutGeometry.TryFreshBounds(item.Value) is Bounds2d rendered
                    ? rendered.Width
                    : desiredWidth;
                inlineItems.Add(new NarrativeInlineItem(
                    item.Topology.EntityHandle,
                    NarrativeInlinePacker.MeasurementSafeWidth(
                        Math.Max(desiredWidth, renderedWidth),
                        safetyScale: 1.01),
                    CadLayoutGeometry.ActualHeight(item.Value)));
            }

            NarrativeInlinePacking packing = NarrativeInlinePacker.Pack(
                inlineItems,
                availableWidth,
                horizontalGap: minimumHeight * 0.08,
                verticalGap: minimumHeight * 0.15);
            packed.Add(new PackedWorkingCluster(startX, packing));
        }

        return new PackedWorkingRow(
            packed,
            packed.Max(cluster => cluster.Packing.Height));
    }

    private static WorkingRow[] BuildRows(
        IReadOnlyList<WorkingText> items,
        double medianHeight)
    {
        IReadOnlyDictionary<string, WorkingText> byId = items
            .ToDictionary(item => item.Target.Manifest.RecordId, StringComparer.Ordinal);
        return NarrativeRowPlanner.Group(
                items.Select(item => new NarrativeRowItem(
                    item.Target.Manifest.RecordId,
                    item.Topology.Source.Bounds)).ToArray(),
                medianHeight)
            .Select(row =>
            {
                NarrativeRowItem[] rowItems = row.MemberIds
                    .Select(id => new NarrativeRowItem(
                        id,
                        byId[id].Topology.Source.Bounds))
                    .ToArray();
                WorkingCluster[] clusters = NarrativeRowClusterPlanner.Partition(
                        rowItems,
                        medianHeight)
                    .Select(cluster => new WorkingCluster(
                        cluster.SourceBounds,
                        cluster.MemberIds.Select(id => byId[id]).ToArray()))
                    .ToArray();
                return new WorkingRow(row.SourceBounds, clusters);
            })
            .ToArray();
    }

    private static double Midpoint(double left, double right) =>
        (left + right) / 2;

    private static double Median(IEnumerable<double> values)
    {
        double[] ordered = values.Where(value => value > 0).OrderBy(value => value).ToArray();
        if (ordered.Length == 0) return 1;
        int middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
    }
}

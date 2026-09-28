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
        string OldHandle)
    {
        // Set when an exploded source row is merged into this text; packing and
        // width estimation must measure the merged line, not the survivor fragment.
        internal string? MergedRaw { get; set; }
    }

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
        var mergedMembers = new List<(WorkingText Member, WorkingText Survivor)>();
        ordered = MergeExplodedRows(ordered, mergedMembers);
        if (ordered.Length == 0)
        {
            return [];
        }

        Rect2 region = allowedOverride ?? ordered[0].Topology.Region!.Bounds;
        double medianHeight = Median(ordered.Select(item => item.OriginalHeight));
        WorkingRow[] rows = BuildRows(ordered, medianHeight);
        Rect2 inner = CadLayoutGeometry.Inset(region, medianHeight * 0.10);
        double startTop = Math.Min(inner.Top, ordered.Max(item => item.Topology.Source.Bounds.Top));
        // A note column is as tall as its frame, so budgeting against the region
        // bottom lets a longer target stack far below the band its own source text
        // occupied and cover unrelated content underneath (detail views, labels,
        // tables). Budget the reflow to the source band plus one line of slack so
        // the ladder shrinks to stay inside the column's own text area instead.
        double sourceBandBottom = ordered.Min(item => item.Topology.Source.Bounds.Bottom);
        // A merged row renders the whole source line as one wrapped block: English
        // prose needs more lines than the Chinese source row it replaces. Budget the
        // extra wrapped lines, otherwise the ladder compresses the row into a bar.
        // The extra budget is capped relative to the source band: a column of many
        // merged rows accumulates an unbounded extra that the ladder can never fit,
        // so it falls through to the emergency floor and the WHOLE column renders
        // at micro height (source uniform 320 -> 80). Cap the extra at 60% of the
        // source band plus one line; the ladder then finds a readable uniform scale.
        double columnWidth = Math.Max(inner.Right - inner.Left, medianHeight);
        double sourceBandSpan = Math.Max(0, startTop - sourceBandBottom);
        double mergedExtraCap = sourceBandSpan * 0.60 + medianHeight;
        double mergedExtraHeight = 0;
        foreach (WorkingText item in ordered)
        {
            string raw = item.MergedRaw ?? item.Target.RestoredText;
            double lines = Math.Ceiling(
                LayoutTextMetrics.EstimateEmWidth(raw) * item.OriginalHeight / columnWidth);
            mergedExtraHeight += Math.Max(0, lines - 1) * item.OriginalHeight * 1.25;
        }

        mergedExtraHeight = Math.Min(mergedExtraHeight, mergedExtraCap);

        double sourceBandBudget = startTop - sourceBandBottom + (medianHeight * 1.5) + mergedExtraHeight;
        double availableHeight = Math.Max(
            medianHeight,
            Math.Min(startTop - inner.Bottom, sourceBandBudget));

        // A target that needs more lines than its source cannot be contained at a
        // floor as high as 0.80; the ladder then finds nothing, and the caller
        // places the group anyway, which is what pushes note text over the views
        // below. SelectScale returns the FIRST (largest) scale that fits, so
        // opening the floor down to the emergency scale only changes the case that
        // would otherwise overflow: the group still renders at the largest scale
        // its own band can hold.
        double effectiveMinimumHeightScale = Math.Min(
            minimumHeightScale,
            LayoutFitPolicy.EmergencyMinimumHeightScale);

        (double widthScale, double heightScale, bool fits) = SelectScale(
            rows,
            inner,
            availableHeight,
            medianHeight,
            effectiveMinimumHeightScale);
        Position(rows, inner, startTop, widthScale, heightScale, medianHeight);
        bool allItemsInside = AllInside(ordered, inner);
        if (LayoutFitPolicy.NeedsContainmentRetry(fits, allItemsInside))
        {
            fits = false;
            widthScale = LayoutFitPolicy.MinimumWidthScale;
            foreach (double retryScale in CadLayoutGeometry.Steps(
                         heightScale,
                         effectiveMinimumHeightScale,
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
                // `working` still contains merged-away fragments whose MTexts are
                // already erased: their bounds never resolve, so the retry ladder
                // always fell through to the emergency floor (whole column at 0.25).
                // Test the surviving texts only.
                if (AllInside(ordered, inner))
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

        // Merged fragments no longer exist as entities; map their records to the
        // survivor so the audit handle map and the candidate verifier resolve them.
        foreach ((WorkingText member, WorkingText survivor) in mergedMembers)
        {
            adjustments.Add(new LayoutAdjustment(
                member.Target.Manifest.RecordId,
                member.OldHandle,
                survivor.Value.Handle.ToString(),
                "AcDbMText",
                ["fragment-merge"],
                member.OriginalHeight,
                survivor.Value.TextHeight,
                member.OriginalWidthFactor,
                widthScale,
                member.Target.SourceBounds,
                CadLayoutGeometry.TryFreshBounds(survivor.Value),
                false,
                "note-column-fragment-merge"));
        }

        foreach (WorkingText item in ordered.Where(item => item.Source is not null))
        {
            item.Source!.Erase();
        }

        return adjustments.ToArray();
    }

    // Note-column source prose is often authored as several entities per visual row
    // (wrapped chunks, one entity per character, standalone punctuation). Reflowing
    // each entity as its own inline item scatters punctuation dots and shrinks the
    // whole stack to the readable floor. Re-join every visual row cluster (one text
    // column) into its leftmost member before packing so the row renders as one line.
    private static WorkingText[] MergeExplodedRows(
        WorkingText[] ordered,
        ICollection<(WorkingText Member, WorkingText Survivor)> merged)
    {
        if (ordered.Length < 2)
        {
            return ordered;
        }

        double medianHeight = Median(ordered.Select(item => item.OriginalHeight));
        Dictionary<string, WorkingText> byId = ordered
            .ToDictionary(item => item.Target.Manifest.RecordId, StringComparer.Ordinal);
        var removed = new HashSet<string>(StringComparer.Ordinal);
        foreach (NarrativeLogicalRow row in NarrativeRowPlanner.Group(
                     ordered.Select(item => new NarrativeRowItem(
                         item.Target.Manifest.RecordId,
                         item.Topology.Source.Bounds)).ToArray(),
                     medianHeight))
        {
            foreach (NarrativeRowCluster cluster in NarrativeRowClusterPlanner.Partition(
                         row.MemberIds.Select(id => new NarrativeRowItem(
                             id,
                             byId[id].Topology.Source.Bounds)).ToArray(),
                         medianHeight))
            {
                if (cluster.MemberIds.Count < 2)
                {
                    continue;
                }

                // Partition already returns members in reading order.
                WorkingText[] inXOrder = cluster.MemberIds.Select(id => byId[id]).ToArray();
                WorkingText survivor = inXOrder[0];
                survivor.MergedRaw = string.Concat(
                    inXOrder.Select(member => member.Target.RestoredText));
                // The survivor's own source bounds cover only its first character;
                // rows/clusters/packing keyed on it would give the merged English
                // line a one-character slot and blow up the column height ladder.
                // Widen the survivor's topology to the union of the whole row.
                double minLeft = inXOrder.Min(m => m.Topology.Source.Bounds.Left);
                double maxRight = inXOrder.Max(m => m.Topology.Source.Bounds.Right);
                double minBottom = inXOrder.Min(m => m.Topology.Source.Bounds.Bottom);
                double maxTop = inXOrder.Max(m => m.Topology.Source.Bounds.Top);
                Rect2 unionBounds = new(minLeft, minBottom, maxRight, maxTop);
                int survivorIndex = Array.IndexOf(ordered, survivor);
                ordered[survivorIndex] = survivor = survivor with
                {
                    Topology = survivor.Topology with
                    {
                        Source = survivor.Topology.Source with { Bounds = unionBounds },
                    },
                    Target = survivor.Target with
                    {
                        SourceBounds = Bounds2d.From(unionBounds),
                    },
                };
                byId[survivor.Target.Manifest.RecordId] = survivor;
                foreach (WorkingText member in inXOrder.Skip(1))
                {
                    member.Value.Erase();
                    member.Source?.Erase();
                    removed.Add(member.Target.Manifest.RecordId);
                    merged.Add((member, survivor));
                }
            }
        }

        return [.. ordered.Where(item => !removed.Contains(item.Target.Manifest.RecordId))];
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

        // Shrinking height keeps glyphs readable; jumping straight to the minimum
        // width factor squeezes every line into dense micro bars. Step the width
        // down only as far as each height rung actually needs.
        foreach (double heightScale in CadLayoutGeometry.Steps(
                     0.95,
                     minimumHeightScale,
                     0.05))
        {
            foreach (double widthScale in CadLayoutGeometry.Steps(
                         1,
                         LayoutFitPolicy.MinimumWidthScale,
                         0.05))
            {
                Prepare(rows, inner, widthScale, heightScale);
                if (RequiredHeight(
                        rows,
                        inner,
                        widthScale,
                        medianHeight * heightScale) <= availableHeight)
                {
                    return (widthScale, heightScale, true);
                }
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
                string raw = item.MergedRaw ?? item.Target.RestoredText;
                string contents = item.Source is null
                    ? raw
                    : NarrativeTargetFontPolicy.ApplyLatinWrapper(raw);
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
        // Same gap policy as Position: keep the source rhythm, floor at 0.2 line.
        double gapHeight = 0;
        WorkingRow? previous = null;
        foreach (WorkingRow row in rows)
        {
            if (previous is not null)
            {
                gapHeight += Clamp(
                    previous.SourceBounds.Bottom - row.SourceBounds.Top,
                    scaledMedianHeight * 0.20,
                    scaledMedianHeight * 1.50);
            }

            previous = row;
        }

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
            // No per-row source-advance cap: English rows legitimately wrap taller
            // than their source line (merged or single-entity). Containment of the
            // whole stack is already guaranteed by the column budget and the scale
            // ladder (RequiredHeight sums real packed heights); capping individual
            // rows only shrank wrapped rows into emergency-floor micro text.
            if (previous is not null)
            {
                // Capped rhythm: sparse regions (headings interleaved with other
                // regions' rows) have huge source gaps; uncapped they blow the
                // column budget and drop the ladder to the emergency floor.
                double sourceGap = previous.SourceBounds.Bottom - row.SourceBounds.Top;
                cursor -= Clamp(
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
            double startX = Clamp(
                cluster.SourceBounds.Left,
                inner.Left,
                maximumStartX);
            double slotRight = clusterIndex + 1 < row.Clusters.Count
                ? Midpoint(
                    cluster.SourceBounds.Right,
                    row.Clusters[clusterIndex + 1].SourceBounds.Left)
                : inner.Right;
            slotRight = Clamp(slotRight, startX + minimumHeight, inner.Right);
            double availableWidth = Math.Max(minimumHeight, slotRight - startX);
            var inlineItems = new List<NarrativeInlineItem>(cluster.Items.Count);
            foreach (WorkingText item in cluster.Items)
            {
                double desiredWidth = Clamp(
                    LayoutTextMetrics.EstimateEmWidth(item.MergedRaw ?? item.Target.RestoredText) *
                    item.Value.TextHeight *
                    widthScale *
                    1.05,
                    item.Value.TextHeight,
                    availableWidth);
                // Give the row its full slot width: a narrow estimated box wraps
                // short headings into two-line micro blocks while neighbours stay
                // single-line. Glyph extents (not the box) drive inline packing.
                item.Value.Width = availableWidth;
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

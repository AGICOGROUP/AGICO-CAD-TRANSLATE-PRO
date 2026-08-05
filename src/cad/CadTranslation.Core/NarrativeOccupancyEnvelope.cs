namespace CadTranslation.Core;

public static class NarrativeOccupancyEnvelope
{
    public static Rect2 ConstrainToSourceVerticalEnvelope(
        Rect2 region,
        IReadOnlyList<Rect2> sourceBounds)
    {
        if (sourceBounds.Count == 0)
        {
            return region;
        }

        double bottom = Math.Max(region.Bottom, sourceBounds.Min(bounds => bounds.Bottom));
        double top = Math.Min(region.Top, sourceBounds.Max(bounds => bounds.Top));
        return top > bottom
            ? new Rect2(region.Left, bottom, region.Right, top)
            : region;
    }

    public static NarrativeOccupancyGroup[] Expand(
        IReadOnlyList<NarrativeOccupancyGroup> groups,
        IReadOnlyList<NarrativeOccupancySample> sourceSamples,
        IReadOnlyList<Rect2> containers,
        double medianTextHeight,
        double tolerance = 1e-6)
    {
        if (groups.Count == 0)
        {
            return [];
        }

        double height = Math.Max(medianTextHeight, tolerance);
        Rect2[] expanded = groups
            .Select(group => ExpandOne(
                group,
                sourceSamples,
                containers,
                height,
                tolerance))
            .ToArray();

        MakeGroupsMutuallyExclusive(
            groups,
            expanded,
            sourceSamples,
            height,
            tolerance);

        return groups
            .Select((group, index) => group with
            {
                Region = group.Region with { Bounds = expanded[index] }
            })
            .ToArray();
    }

    private static Rect2 ExpandOne(
        NarrativeOccupancyGroup group,
        IReadOnlyList<NarrativeOccupancySample> sourceSamples,
        IReadOnlyList<Rect2> containers,
        double height,
        double tolerance)
    {
        Rect2 source = group.Region.Bounds;
        Rect2? container = containers
            .Where(value => value.Contains(source, tolerance))
            .OrderBy(value => value.Area)
            .Cast<Rect2?>()
            .FirstOrDefault();
        Rect2 available = container is Rect2 frame
            ? InsetWithoutClippingSource(frame, source, height * 0.10)
            : new Rect2(
                source.Left - height * 4,
                source.Bottom - height * 2,
                source.Right + height * 4,
                source.Top + height * 2);

        var memberIds = new HashSet<string>(group.MemberIds, StringComparer.Ordinal);
        foreach (NarrativeOccupancySample obstacle in sourceSamples.Where(sample => !memberIds.Contains(sample.Id)))
        {
            Rect2 bounds = obstacle.Bounds;
            if (VerticallyOverlaps(source, bounds, tolerance))
            {
                if (bounds.Right <= source.Left + tolerance)
                {
                    available = new Rect2(
                        Math.Max(available.Left, Midpoint(bounds.Right, source.Left)),
                        available.Bottom,
                        available.Right,
                        available.Top);
                }
                else if (bounds.Left >= source.Right - tolerance)
                {
                    available = new Rect2(
                        available.Left,
                        available.Bottom,
                        Math.Min(available.Right, Midpoint(source.Right, bounds.Left)),
                        available.Top);
                }
            }

            if (HorizontallyOverlaps(source, bounds, tolerance))
            {
                if (bounds.Bottom >= source.Top - tolerance)
                {
                    available = new Rect2(
                        available.Left,
                        available.Bottom,
                        available.Right,
                        Math.Min(available.Top, Midpoint(source.Top, bounds.Bottom)));
                }
                else if (bounds.Top <= source.Bottom + tolerance)
                {
                    available = new Rect2(
                        available.Left,
                        Math.Max(available.Bottom, Midpoint(bounds.Top, source.Bottom)),
                        available.Right,
                        available.Top);
                }
            }
        }

        return EnsureContains(available, source);
    }

    private static void MakeGroupsMutuallyExclusive(
        IReadOnlyList<NarrativeOccupancyGroup> groups,
        Rect2[] expanded,
        IReadOnlyList<NarrativeOccupancySample> sourceSamples,
        double height,
        double tolerance)
    {
        IReadOnlyDictionary<string, NarrativeOccupancySample> sampleById =
            sourceSamples.ToDictionary(sample => sample.Id, StringComparer.Ordinal);
        double gutter = height * 0.10;
        for (int leftIndex = 0; leftIndex < groups.Count; leftIndex++)
        {
            for (int rightIndex = leftIndex + 1; rightIndex < groups.Count; rightIndex++)
            {
                NarrativeOccupancySample[] leftSamples = groups[leftIndex].MemberIds
                    .Where(sampleById.ContainsKey)
                    .Select(id => sampleById[id])
                    .ToArray();
                NarrativeOccupancySample[] rightSamples = groups[rightIndex].MemberIds
                    .Where(sampleById.ContainsKey)
                    .Select(id => sampleById[id])
                    .ToArray();
                if (leftSamples.Length == 0 || rightSamples.Length == 0)
                {
                    continue;
                }

                double leftMinimumX = leftSamples.Min(sample => sample.Anchor.X);
                double leftMaximumX = leftSamples.Max(sample => sample.Anchor.X);
                double rightMinimumX = rightSamples.Min(sample => sample.Anchor.X);
                double rightMaximumX = rightSamples.Max(sample => sample.Anchor.X);
                if (leftMaximumX < rightMinimumX - tolerance ||
                    rightMaximumX < leftMinimumX - tolerance)
                {
                    int firstIndex = leftMaximumX < rightMinimumX
                        ? leftIndex
                        : rightIndex;
                    int secondIndex = firstIndex == leftIndex ? rightIndex : leftIndex;
                    double firstMaximumX = firstIndex == leftIndex ? leftMaximumX : rightMaximumX;
                    double secondMinimumX = secondIndex == leftIndex ? leftMinimumX : rightMinimumX;
                    if (!VerticallyOverlaps(
                            expanded[firstIndex],
                            expanded[secondIndex],
                            tolerance) ||
                        expanded[firstIndex].Right <
                        expanded[secondIndex].Left - gutter * 2 - tolerance)
                    {
                        continue;
                    }

                    Rect2 firstSource = groups[firstIndex].Region.Bounds;
                    Rect2 secondSource = groups[secondIndex].Region.Bounds;
                    double boundary = firstSource.Right < secondSource.Left - tolerance
                        ? Midpoint(firstSource.Right, secondSource.Left)
                        : Midpoint(firstMaximumX, secondMinimumX);
                    Rect2 first = expanded[firstIndex];
                    Rect2 second = expanded[secondIndex];
                    expanded[firstIndex] = new Rect2(
                        first.Left,
                        first.Bottom,
                        Math.Max(first.Left + height, Math.Min(first.Right, boundary - gutter)),
                        first.Top);
                    expanded[secondIndex] = new Rect2(
                        Math.Min(second.Right - height, Math.Max(second.Left, boundary + gutter)),
                        second.Bottom,
                        second.Right,
                        second.Top);
                    continue;
                }

                double leftMinimumY = leftSamples.Min(sample => sample.Anchor.Y);
                double leftMaximumY = leftSamples.Max(sample => sample.Anchor.Y);
                double rightMinimumY = rightSamples.Min(sample => sample.Anchor.Y);
                double rightMaximumY = rightSamples.Max(sample => sample.Anchor.Y);
                if (leftMaximumY >= rightMinimumY - tolerance &&
                    rightMaximumY >= leftMinimumY - tolerance)
                {
                    continue;
                }

                int lowerIndex = leftMaximumY < rightMinimumY
                    ? leftIndex
                    : rightIndex;
                int upperIndex = lowerIndex == leftIndex ? rightIndex : leftIndex;
                double lowerMaximumY = lowerIndex == leftIndex ? leftMaximumY : rightMaximumY;
                double upperMinimumY = upperIndex == leftIndex ? leftMinimumY : rightMinimumY;
                if (!HorizontallyOverlaps(
                        expanded[lowerIndex],
                        expanded[upperIndex],
                        tolerance) ||
                    expanded[lowerIndex].Top <
                    expanded[upperIndex].Bottom - gutter * 2 - tolerance)
                {
                    continue;
                }

                Rect2 lowerSource = groups[lowerIndex].Region.Bounds;
                Rect2 upperSource = groups[upperIndex].Region.Bounds;
                double verticalBoundary = lowerSource.Top < upperSource.Bottom - tolerance
                    ? Midpoint(lowerSource.Top, upperSource.Bottom)
                    : Midpoint(lowerMaximumY, upperMinimumY);
                Rect2 lower = expanded[lowerIndex];
                Rect2 upper = expanded[upperIndex];
                expanded[lowerIndex] = new Rect2(
                    lower.Left,
                    lower.Bottom,
                    lower.Right,
                    Math.Max(lower.Bottom + height, Math.Min(lower.Top, verticalBoundary - gutter)));
                expanded[upperIndex] = new Rect2(
                    upper.Left,
                    Math.Min(upper.Top - height, Math.Max(upper.Bottom, verticalBoundary + gutter)),
                    upper.Right,
                    upper.Top);
            }
        }
    }

    private static Rect2 InsetWithoutClippingSource(
        Rect2 container,
        Rect2 source,
        double inset) =>
        EnsureContains(
            new Rect2(
                container.Left + inset,
                container.Bottom + inset,
                container.Right - inset,
                container.Top - inset),
            source);

    private static Rect2 EnsureContains(Rect2 candidate, Rect2 source) =>
        new(
            Math.Min(candidate.Left, source.Left),
            Math.Min(candidate.Bottom, source.Bottom),
            Math.Max(candidate.Right, source.Right),
            Math.Max(candidate.Top, source.Top));

    private static bool HorizontallyOverlaps(Rect2 left, Rect2 right, double tolerance) =>
        Math.Min(left.Right, right.Right) - Math.Max(left.Left, right.Left) > tolerance;

    private static bool VerticallyOverlaps(Rect2 left, Rect2 right, double tolerance) =>
        Math.Min(left.Top, right.Top) - Math.Max(left.Bottom, right.Bottom) > tolerance;

    private static double Midpoint(double left, double right) => (left + right) / 2;
}

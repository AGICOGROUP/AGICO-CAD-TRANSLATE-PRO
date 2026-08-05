namespace CadTranslation.Core;

public sealed record NarrativeRowItem(string Id, Rect2 SourceBounds);

public sealed record NarrativeLogicalRow(
    IReadOnlyList<string> MemberIds,
    Rect2 SourceBounds);

public static class NarrativeHorizontalPanelPartitioner
{
    public static NarrativeRowItem[][] Partition(
        IReadOnlyList<NarrativeRowItem> items,
        double medianTextHeight)
    {
        if (items.Count == 0)
        {
            return [];
        }

        double splitGap = Math.Max(1e-6, medianTextHeight) * 20;
        var panels = new List<List<NarrativeRowItem>> { new() };
        double? previousLeft = null;
        foreach (NarrativeRowItem item in items
                     .OrderBy(item => item.SourceBounds.Left)
                     .ThenByDescending(item => item.SourceBounds.Top))
        {
            if (previousLeft is not null &&
                item.SourceBounds.Left - previousLeft.Value > splitGap)
            {
                panels.Add(new List<NarrativeRowItem>());
            }
            panels[^1].Add(item);
            previousLeft = item.SourceBounds.Left;
        }

        return panels
            .Select(panel => panel
                .OrderByDescending(item => item.SourceBounds.Top)
                .ThenBy(item => item.SourceBounds.Left)
                .ToArray())
            .ToArray();
    }
}

public sealed record NarrativeRegionEnvelope(double Left, double Bottom, double Top);

public static class NarrativeRegionRightBoundary
{
    public static double Resolve(
        double left,
        double bottom,
        double top,
        double medianTextHeight,
        IReadOnlyList<double> sourceRights,
        IReadOnlyList<NarrativeRegionEnvelope> followingRegions)
    {
        if (sourceRights.Count == 0)
        {
            throw new ArgumentException("At least one source right boundary is required.", nameof(sourceRights));
        }

        double[] orderedRights = sourceRights.OrderBy(value => value).ToArray();
        int percentileIndex = (int)Math.Round((orderedRights.Length - 1) * 0.95);
        double right = orderedRights[Math.Clamp(percentileIndex, 0, orderedRights.Length - 1)];
        NarrativeRegionEnvelope? neighbor = followingRegions
            .Where(region => region.Top > bottom && region.Bottom < top)
            .OrderBy(region => region.Left)
            .FirstOrDefault();
        if (neighbor is not null)
        {
            right = Math.Min(right, neighbor.Left - medianTextHeight * 0.50);
        }

        return Math.Max(right, left + medianTextHeight * 8);
    }
}

public static class NarrativeRowPlanner
{
    public static NarrativeLogicalRow[] Group(
        IReadOnlyList<NarrativeRowItem> items,
        double medianTextHeight,
        double tolerance = 1e-6)
    {
        double height = Math.Max(medianTextHeight, tolerance);
        var rows = new List<List<NarrativeRowItem>>();
        foreach (NarrativeRowItem item in items
                     .OrderByDescending(item => item.SourceBounds.Center.Y)
                     .ThenBy(item => item.SourceBounds.Left))
        {
            List<NarrativeRowItem>? row = rows.LastOrDefault();
            double rowCenter = row is null || row.Count == 0
                ? double.NaN
                : row.Average(value => value.SourceBounds.Center.Y);
            if (row is null ||
                Math.Abs(rowCenter - item.SourceBounds.Center.Y) >
                height * 0.50 + tolerance)
            {
                row = [];
                rows.Add(row);
            }

            row.Add(item);
        }

        return rows
            .Select(row =>
            {
                NarrativeRowItem[] ordered = row
                    .OrderBy(item => item.SourceBounds.Left)
                    .ThenBy(item => item.Id, StringComparer.Ordinal)
                    .ToArray();
                return new NarrativeLogicalRow(
                    ordered.Select(item => item.Id).ToArray(),
                    Union(ordered.Select(item => item.SourceBounds)));
            })
            .ToArray();
    }

    private static Rect2 Union(IEnumerable<Rect2> values)
    {
        Rect2[] items = values.ToArray();
        return new Rect2(
            items.Min(item => item.Left),
            items.Min(item => item.Bottom),
            items.Max(item => item.Right),
            items.Max(item => item.Top));
    }
}

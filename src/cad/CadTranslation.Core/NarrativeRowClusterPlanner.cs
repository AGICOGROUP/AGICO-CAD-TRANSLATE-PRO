namespace CadTranslation.Core;

public sealed record NarrativeRowCluster(
    IReadOnlyList<string> MemberIds,
    Rect2 SourceBounds);

public static class NarrativeRowClusterPlanner
{
    public static NarrativeRowCluster[] Partition(
        IReadOnlyList<NarrativeRowItem> items,
        double medianTextHeight,
        double tolerance = 1e-6)
    {
        if (items.Count == 0)
        {
            return [];
        }

        double height = Math.Max(medianTextHeight, tolerance);
        double splitGap = height * 4;
        var clusters = new List<List<NarrativeRowItem>>();
        var current = new List<NarrativeRowItem>();
        double currentRight = double.NaN;
        foreach (NarrativeRowItem item in items
                     .OrderBy(item => item.SourceBounds.Left)
                     .ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            if (current.Count > 0 &&
                item.SourceBounds.Left - currentRight > splitGap + tolerance)
            {
                clusters.Add(current);
                current = [];
                currentRight = double.NaN;
            }

            current.Add(item);
            currentRight = double.IsNaN(currentRight)
                ? item.SourceBounds.Right
                : Math.Max(currentRight, item.SourceBounds.Right);
        }

        if (current.Count > 0)
        {
            clusters.Add(current);
        }

        return clusters.Select(cluster => new NarrativeRowCluster(
                cluster.Select(item => item.Id).ToArray(),
                Union(cluster.Select(item => item.SourceBounds))))
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

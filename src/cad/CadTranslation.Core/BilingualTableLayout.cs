namespace CadTranslation.Core;

/// <summary>Connected grid cells only; isolated frames and free labels are not tables.</summary>
public static class BilingualTableLayout
{
    public static IReadOnlyList<Rect2[]> Groups(IEnumerable<LayoutRegion> regions)
    {
        var remaining = regions.Where(r => r.Kind == LayoutRegionKind.TableCell).Select(r => r.Bounds).Distinct().ToList();
        var result = new List<Rect2[]>();
        while (remaining.Count > 0)
        {
            var group = new List<Rect2> { remaining[0] };
            remaining.RemoveAt(0);
            for (int i = 0; i < group.Count; i++)
                for (int j = remaining.Count - 1; j >= 0; j--)
                    if (Adjacent(group[i], remaining[j])) { group.Add(remaining[j]); remaining.RemoveAt(j); }
            if (group.Count >= 2) result.Add(group.ToArray());
        }
        return result;
    }

    private static bool Adjacent(Rect2 a, Rect2 b) =>
        ((Math.Abs(a.Right - b.Left) < 1e-5 || Math.Abs(b.Right - a.Left) < 1e-5) && Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom) > 1e-5) ||
        ((Math.Abs(a.Top - b.Bottom) < 1e-5 || Math.Abs(b.Top - a.Bottom) < 1e-5) && Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left) > 1e-5);

    public static Rect2[] SideSlots(Rect2 table, IReadOnlyList<Rect2> rows, double width, double gap, bool left) =>
        rows.Select(r => new Rect2(left ? table.Left - gap - width : table.Right + gap,
            r.Bottom + gap, left ? table.Left - gap : table.Right + gap + width, r.Top - gap)).ToArray();
}

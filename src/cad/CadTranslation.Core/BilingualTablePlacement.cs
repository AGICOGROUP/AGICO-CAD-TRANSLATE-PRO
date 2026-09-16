namespace CadTranslation.Core;

/// <summary>Two table choices: a readable same-cell suffix, or a touching-side copy.</summary>
public static class BilingualTablePlacement
{
    public static Rect2? AfterSource(Rect2 cell, Rect2 source, double width, double height,
        double sourceHeight, IEnumerable<Rect2> occupied)
    {
        if (width <= 0 || height <= 0 || sourceHeight <= 0) return null;
        double gap = sourceHeight * .25;
        if(cell.Width<=gap || cell.Height<=gap) return null;
        var target = new Rect2(source.Right + gap, source.Center.Y - height / 2,
            source.Right + gap + width, source.Center.Y + height / 2);
        if (!new Rect2(cell.Left + gap / 2, cell.Bottom + gap / 2,
            cell.Right - gap / 2, cell.Top - gap / 2).Contains(target)) return null;
        return occupied.Any(b => Overlap(target, b, gap / 2)) ? null : target;
    }

    public static Rect2[] AdjacentCopies(Rect2 table, double gap) =>
    [
        new(table.Right + gap, table.Bottom, table.Right + gap + table.Width, table.Top),
        new(table.Left - gap - table.Width, table.Bottom, table.Left - gap, table.Top),
        new(table.Left, table.Top + gap, table.Right, table.Top + gap + table.Height),
        new(table.Left, table.Bottom - gap - table.Height, table.Right, table.Bottom - gap)
    ];

    public static IReadOnlyList<Rect2> AdjacentCopySearch(Rect2 table, double gap, IReadOnlyList<Rect2>? frames = null)
    {
        var result = new List<Rect2>();
        var primary = AdjacentCopies(table, gap);
        result.AddRange(primary);
        foreach (double fraction in new[] { -.5, -.25, .25, .5 })
        {
            double dy = table.Height * fraction, dx = table.Width * fraction;
            result.Add(new(primary[0].Left, primary[0].Bottom + dy, primary[0].Right, primary[0].Top + dy));
            result.Add(new(primary[1].Left, primary[1].Bottom + dy, primary[1].Right, primary[1].Top + dy));
            result.Add(new(primary[2].Left + dx, primary[2].Bottom, primary[2].Right + dx, primary[2].Top));
            result.Add(new(primary[3].Left + dx, primary[3].Bottom, primary[3].Right + dx, primary[3].Top));
        }
        result.AddRange(BilingualPanelPlacement.OutsideFrames(table,table.Width,table.Height,gap,frames ?? []));
        return result.Where(b=>BilingualPanelPlacement.AvoidsOtherFrames(table,b,frames ?? [])).ToArray();
    }

    public static bool Overlap(Rect2 a, Rect2 b, double gap = 0) =>
        a.Left < b.Right + gap && a.Right > b.Left - gap && a.Bottom < b.Top + gap && a.Top > b.Bottom - gap;
}

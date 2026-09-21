namespace CadTranslation.Core;

/// <summary>Complete translated copies, aligned beside or above/below the source.</summary>
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

    public static IReadOnlyList<Rect2> CompleteCopySearch(Rect2 table, double gap,
        IReadOnlyList<Rect2> occupied, IReadOnlyList<Rect2> frames)
        => AdjacentCopySearch(table, gap, frames);

    public static IReadOnlyList<Rect2> AdjacentCopySearch(Rect2 table, double gap, IReadOnlyList<Rect2>? frames = null)
    {
        frames ??= [];
        var own = frames.Where(f => IsContainingFrame(table, f)).OrderBy(f => f.Area).FirstOrDefault();
        bool hasFrame = own.Area > 0;
        var adjacent = AdjacentCopies(table, gap);
        // Tall tables attach horizontally; wide tables attach vertically.
        // Within that axis, move toward the sheet interior before its exterior.
        bool inwardRight = !hasFrame || table.Center.X <= own.Center.X;
        bool inwardUp = hasFrame && table.Center.Y <= own.Center.Y;
        int[] order = table.Width >= table.Height
            ? [inwardUp ? 2 : 3, inwardUp ? 3 : 2, inwardRight ? 0 : 1, inwardRight ? 1 : 0]
            : [inwardRight ? 0 : 1, inwardRight ? 1 : 0, inwardUp ? 2 : 3, inwardUp ? 3 : 2];
        return order.Select(i => adjacent[i])
            .Where(candidate => AvoidsOtherTableFrames(table, candidate, frames))
            .ToArray();
    }

    public static bool AvoidsOtherTableFrames(Rect2 table, Rect2 candidate, IEnumerable<Rect2> frames) =>
        !frames.Any(f => !IsContainingFrame(table, f) && Overlap(f, candidate));

    public static bool IsContainingFrame(Rect2 table, Rect2 frame) => frame.Area > table.Area * 1.5 &&
        frame.Left <= table.Center.X && frame.Right >= table.Center.X &&
        frame.Bottom <= table.Center.Y && frame.Top >= table.Center.Y;

    public static bool Overlap(Rect2 a, Rect2 b, double gap = 0) =>
        a.Left < b.Right + gap && a.Right > b.Left - gap && a.Bottom < b.Top + gap && a.Top > b.Bottom - gap;
}

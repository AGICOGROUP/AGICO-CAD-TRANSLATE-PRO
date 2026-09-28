namespace CadTranslation.Core;

public static class ReplaceLocalPlacement
{
    // Cross-region (cell overflow) findings are repairable by the same bounded move
    // as text overlaps, so they join the selection instead of being left for review.
    public static string[] Select(IEnumerable<(string Id, string? OtherId, string Code, string Level)> risks,
        IEnumerable<string> changed)
    {
        var eligible = changed.ToHashSet(StringComparer.Ordinal);
        return risks.Where(r => r.Level == "high" &&
                r.Code is "text-overlap" or "geometry-contact" or "cross-region")
            .SelectMany(r => new[] { r.Id, r.OtherId }).OfType<string>()
            .Where(eligible.Contains).Distinct().OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }

    // `grid` bounds the search budget. The sweep stays available at every rung of
    // the shrink ladder - the wide scale-1.0 box rarely fits anywhere, so an
    // anchors-only deep rung leaves merged-cell labels unrepairable - but it is
    // capped at 48 cells, roughly half the cost the 23-sheet civil test already
    // tolerates in one pass.
    public static IReadOnlyList<Rect2> Candidates(Rect2 source, double width, double height,
        double originalHeight, Rect2 allowed, bool grid = true)
    {
        if (width <= 0 || height <= 0 || originalHeight <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        double gap = originalHeight * .25;
        double reach = originalHeight * 6;
        var seen = new HashSet<Rect2>();
        var list = new List<Rect2>();
        void Add(Point2 origin)
        {
            var box = new Rect2(origin.X, origin.Y, origin.X + width, origin.Y + height);
            if (!allowed.Contains(box)) return;
            if (Math.Abs(box.Center.X - source.Center.X) > reach ||
                Math.Abs(box.Center.Y - source.Center.Y) > reach) return;
            if (seen.Add(box)) list.Add(box);
        }
        Add(new Point2(source.Left, source.Bottom));
        Add(new Point2(source.Left, source.Top + gap));
        Add(new Point2(source.Left, source.Bottom - gap - height));
        Add(new Point2(source.Right + gap, source.Bottom));
        Add(new Point2(source.Left - gap - width, source.Bottom));
        // Dense note columns leave no gap at the four immediate neighbours, so a
        // bounded grid inside the same reachable window is searched as well. The
        // grid step doubles until the scan stays small: a repair may reflow within
        // reach of its source, never teleport.
        if (!grid)
        {
            return list
                .OrderBy(r => Math.Pow(r.Center.X - source.Center.X, 2) + Math.Pow(r.Center.Y - source.Center.Y, 2))
                .Take(128).ToArray();
        }
        double leftMin = Math.Max(allowed.Left, source.Center.X - reach - width);
        double leftMax = Math.Min(allowed.Right - width, source.Center.X + reach);
        double bottomMin = Math.Max(allowed.Bottom, source.Center.Y - reach - height);
        double bottomMax = Math.Min(allowed.Top - height, source.Center.Y + reach);
        if (leftMax >= leftMin && bottomMax >= bottomMin)
        {
            double stepX = Math.Max(gap, width / 2), stepY = Math.Max(gap, height / 2);
            int columns = 1 + (int)((leftMax - leftMin) / stepX);
            int rows = 1 + (int)((bottomMax - bottomMin) / stepY);
            while (columns * rows > 48 && (stepX < leftMax - leftMin || stepY < bottomMax - bottomMin))
            {
                stepX *= 2; stepY *= 2;
                columns = 1 + (int)((leftMax - leftMin) / stepX);
                rows = 1 + (int)((bottomMax - bottomMin) / stepY);
            }
            for (int i = 0; i < columns; i++)
                for (int j = 0; j < rows; j++)
                    Add(new Point2(leftMin + i * stepX, bottomMin + j * stepY));
        }
        return list
            .OrderBy(r => Math.Pow(r.Center.X - source.Center.X, 2) + Math.Pow(r.Center.Y - source.Center.Y, 2))
            .Take(48).ToArray();
    }
}

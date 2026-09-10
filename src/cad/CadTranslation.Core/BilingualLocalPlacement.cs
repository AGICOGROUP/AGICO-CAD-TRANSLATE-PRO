namespace CadTranslation.Core;

public static class BilingualLocalPlacement
{
    public static IReadOnlyList<Rect2> Candidates(Rect2 allowed, Rect2 source, double width,
        double height, IReadOnlyList<Rect2> obstacles, double margin)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || !double.IsFinite(margin)
            || width <= 0 || height <= 0 || margin < 0)
            return Array.Empty<Rect2>();

        // Bound search around this label even when its only container is a large drawing frame.
        double reachX = Math.Max(source.Width, width) * 4;
        double reachY = Math.Max(source.Height, height) * 4;
        double left = Math.Max(allowed.Left + margin, source.Left - reachX);
        double right = Math.Min(allowed.Right - margin, source.Right + reachX);
        double bottom = Math.Max(allowed.Bottom + margin, source.Bottom - reachY);
        double top = Math.Min(allowed.Top - margin, source.Top + reachY);
        if (right - left < width || top - bottom < height) return Array.Empty<Rect2>();

        double centerX = source.Center.X - width / 2;
        double centerY = source.Center.Y - height / 2;
        var xs = new List<double> { left, right - width, source.Left, source.Right - width,
            centerX, source.Left - margin - width, source.Right + margin };
        var ys = new List<double> { bottom, top - height, source.Bottom, source.Top - height,
            centerY, source.Bottom - margin - height, source.Top + margin };
        var local = new Rect2(left, bottom, right, top);
        // Only nearby obstacle edges seed anchors; all supplied obstacles still veto candidates.
        foreach (Rect2 obstacle in obstacles.Where(o => Overlaps(local, o, margin))
            .OrderBy(o => Distance(o, source)).Take(32))
        {
            xs.Add(obstacle.Left - margin - width);
            xs.Add(obstacle.Right + margin);
            ys.Add(obstacle.Bottom - margin - height);
            ys.Add(obstacle.Top + margin);
        }
        double[] xAnchors = xs.Select(x => Math.Clamp(x, left, right - width)).Distinct()
            .OrderBy(x => Math.Abs(x - centerX)).Take(16).ToArray();
        double[] yAnchors = ys.Select(y => Math.Clamp(y, bottom, top - height)).Distinct()
            .OrderBy(y => Math.Abs(y - centerY)).Take(16).ToArray();

        // At most 256 pairs, independent of drawing complexity; return at most 128 safe slots.
        return xAnchors.SelectMany(x => yAnchors.Select(y => new Rect2(x, y, x + width, y + height)))
            .Where(r => allowed.Contains(r, 0) && !obstacles.Any(o => Overlaps(r, o, margin)))
            .OrderBy(r => Distance(r, source)).Take(128).ToArray();
    }

    private static double Distance(Rect2 first, Rect2 second) =>
        Math.Pow(first.Center.X - second.Center.X, 2) + Math.Pow(first.Center.Y - second.Center.Y, 2);

    private static bool Overlaps(Rect2 first, Rect2 second, double margin) =>
        first.Left < second.Right + margin && first.Right > second.Left - margin
        && first.Bottom < second.Top + margin && first.Top > second.Bottom - margin;
}

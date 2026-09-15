namespace CadTranslation.Core;

public static class BilingualLocalPlacement
{
    // Edge-to-edge distance: a long translation next to its label is still near.
    public static double Gap(Rect2 source, Rect2 target) => Math.Sqrt(
        Math.Pow(Math.Max(0, Math.Max(source.Left-target.Right, target.Left-source.Right)),2) +
        Math.Pow(Math.Max(0, Math.Max(source.Bottom-target.Top, target.Bottom-source.Top)),2));

    public static IReadOnlyList<Rect2> Candidates(Rect2 allowed, Rect2 source, double width,
        double height, IReadOnlyList<Rect2> obstacles, double margin, IReadOnlyList<Rect2>? anchorHints = null,
        Func<Rect2, bool>? isAvailable = null)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || !double.IsFinite(margin)
            || width <= 0 || height <= 0 || margin < 0)
            return Array.Empty<Rect2>();

        // Keep generated anchors just outside the checked clearance. Subtracting
        // and adding a wrapped height must not round a safe gap into a collision.
        double clearance = margin + Math.Max(1e-8, margin * 1e-6);

        // Bound search around this label even when its only container is a large drawing frame.
        double reachX = Math.Max(source.Width, width) * 4;
        double reachY = Math.Max(source.Height, height) * 4;
        double left = Math.Max(allowed.Left + clearance, source.Left - reachX);
        double right = Math.Min(allowed.Right - clearance, source.Right + reachX);
        double bottom = Math.Max(allowed.Bottom + clearance, source.Bottom - reachY);
        double top = Math.Min(allowed.Top - clearance, source.Top + reachY);
        if (right - left < width || top - bottom < height) return Array.Empty<Rect2>();

        double centerX = source.Center.X - width / 2;
        double centerY = source.Center.Y - height / 2;
        var xs = new List<double> { left, right - width, source.Left, source.Right - width,
            centerX, source.Left - clearance - width, source.Right + clearance };
        var ys = new List<double> { bottom, top - height, source.Bottom, source.Top - height,
            centerY, source.Bottom - clearance - height, source.Top + clearance };
        double[] essentialX=xs.ToArray(), essentialY=ys.ToArray();
        var local = new Rect2(left, bottom, right, top);
        // Only nearby obstacle edges seed anchors; all supplied obstacles still veto candidates.
        foreach (Rect2 obstacle in obstacles.Concat(anchorHints ?? Array.Empty<Rect2>()).Where(o => Overlaps(local, o, margin))
            .OrderBy(o => Distance(o, source)).Take(32))
        {
            xs.Add(obstacle.Left - clearance - width);
            xs.Add(obstacle.Right + clearance);
            ys.Add(obstacle.Bottom - clearance - height);
            ys.Add(obstacle.Top + clearance);
        }
        double[] SelectAnchors(IEnumerable<double> all, IEnumerable<double> essential, double min, double max, double center) =>
            essential.Select(x=>Math.Clamp(x,min,max)).Concat(all.Select(x=>Math.Clamp(x,min,max))
                .OrderBy(x=>Math.Abs(x-center))
                .GroupBy(x=>Math.Round((x-center)/Math.Max(1e-9,margin*.02)))
                .Select(g=>g.First()).Take(16)).Distinct().ToArray();
        double[] xAnchors = SelectAnchors(xs,essentialX,left,right-width,centerX);
        double[] yAnchors = SelectAnchors(ys,essentialY,bottom,top-height,centerY);

        // Preserve seven essential side/edge anchors before the bounded extra
        // hints. Center-based truncation alone can retain only occupied positions.
        // At most 529 pairs; return at most 128 safe slots.
        return xAnchors.SelectMany(x => yAnchors.Select(y => new Rect2(x, y, x + width, y + height)))
            .Where(r => allowed.Contains(r, 0) && !obstacles.Any(o => Overlaps(r, o, margin)) && (isAvailable?.Invoke(r) ?? true))
            .OrderBy(r => Distance(r, source)).Take(128).ToArray();
    }

    private static double Distance(Rect2 first, Rect2 second) =>
        Math.Pow(first.Center.X - second.Center.X, 2) + Math.Pow(first.Center.Y - second.Center.Y, 2);

    private static bool Overlaps(Rect2 first, Rect2 second, double margin) =>
        first.Left < second.Right + margin && first.Right > second.Left - margin
        && first.Bottom < second.Top + margin && first.Top > second.Bottom - margin;
}

namespace CadTranslation.Core;

public static class ReplaceLocalPlacement
{
    public static string[] Select(IEnumerable<(string Id, string? OtherId, string Code, string Level)> risks,
        IEnumerable<string> changed)
    {
        var eligible = changed.ToHashSet(StringComparer.Ordinal);
        return risks.Where(r => r.Level == "high" && r.Code is "text-overlap" or "geometry-contact")
            .SelectMany(r => new[] { r.Id, r.OtherId }).OfType<string>()
            .Where(eligible.Contains).Distinct().OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<Rect2> Candidates(Rect2 source, double width, double height,
        double originalHeight, Rect2 allowed)
    {
        if (width <= 0 || height <= 0 || originalHeight <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        double gap = originalHeight * .25;
        Point2[] origins = [new(source.Left, source.Bottom),
            new(source.Left, source.Top + gap), new(source.Left, source.Bottom - gap - height),
            new(source.Right + gap, source.Bottom), new(source.Left - gap - width, source.Bottom)];
        return origins.Select(p => new Rect2(p.X, p.Y, p.X + width, p.Y + height))
            .Where(r => allowed.Contains(r) && Math.Abs(r.Center.X - source.Center.X) <= originalHeight * 6
                && Math.Abs(r.Center.Y - source.Center.Y) <= originalHeight * 6).Distinct().ToArray();
    }
}

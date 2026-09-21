namespace CadTranslation.Core;

public enum LayoutRegionKind
{
    TableCell,
    ClosedFrame,
    NoteColumn,
    TitleBlock,
    Generic,
    Unassigned
}

public sealed record LayoutRegion(string Id, LayoutRegionKind Kind, Rect2 Bounds);

public sealed record TextLayoutSnapshot(
    string Id,
    Rect2 Bounds,
    Point2 Anchor,
    double OriginalTextHeight,
    double WidthFactor = 1);

public static class RegionAssigner
{
    public static LayoutRegion? Assign(
        TextLayoutSnapshot text,
        IReadOnlyList<LayoutRegion> regions,
        double tolerance = 1e-6) =>
        regions
            .Where(region => IsTrustworthy(region.Kind))
            .Where(region => region.Bounds.Contains(text.Bounds, tolerance))
            .OrderBy(region => region.Bounds.Area)
            .ThenBy(region => Priority(region.Kind))
            .FirstOrDefault();

    private static bool IsTrustworthy(LayoutRegionKind kind) =>
        kind is not LayoutRegionKind.Generic and not LayoutRegionKind.Unassigned;

    private static int Priority(LayoutRegionKind kind) => kind switch
    {
        LayoutRegionKind.TableCell => 0,
        LayoutRegionKind.TitleBlock => 1,
        LayoutRegionKind.NoteColumn => 2,
        LayoutRegionKind.ClosedFrame => 3,
        _ => 4
    };
}

public static class GridCellDetector
{
    // Local ray boundaries recover merged cells split by unrelated global grid ticks.
    public static LayoutRegion? DetectContaining(IReadOnlyList<Segment2> segments, Rect2 text, double tolerance = 1e-6)
    {
        var vertical = segments.Where(s => s.IsVertical(tolerance) && s.MinY <= text.Center.Y && s.MaxY >= text.Center.Y).ToArray();
        var horizontal = segments.Where(s => s.IsHorizontal(tolerance) && s.MinX <= text.Center.X && s.MaxX >= text.Center.X).ToArray();
        double left = vertical.Where(s => s.Start.X <= text.Left + tolerance).Select(s => s.Start.X).DefaultIfEmpty(double.NaN).Max();
        double right = vertical.Where(s => s.Start.X >= text.Right - tolerance).Select(s => s.Start.X).DefaultIfEmpty(double.NaN).Min();
        double bottom = horizontal.Where(s => s.Start.Y <= text.Bottom + tolerance).Select(s => s.Start.Y).DefaultIfEmpty(double.NaN).Max();
        double top = horizontal.Where(s => s.Start.Y >= text.Top - tolerance).Select(s => s.Start.Y).DefaultIfEmpty(double.NaN).Min();
        if (!double.IsFinite(left + right + bottom + top) || right <= left || top <= bottom) return null;
        if (!HasVerticalBoundary(vertical, left, bottom, top, tolerance) || !HasVerticalBoundary(vertical, right, bottom, top, tolerance) ||
            !HasHorizontalBoundary(horizontal, bottom, left, right, tolerance) || !HasHorizontalBoundary(horizontal, top, left, right, tolerance)) return null;
        if (segments.Any(s => s.IsVertical(tolerance) && s.Start.X > left + tolerance && s.Start.X < right - tolerance && s.MaxY > bottom + tolerance && s.MinY < top - tolerance ||
            s.IsHorizontal(tolerance) && s.Start.Y > bottom + tolerance && s.Start.Y < top - tolerance && s.MaxX > left + tolerance && s.MinX < right - tolerance)) return null;
        return new LayoutRegion("merged-cell", LayoutRegionKind.TableCell, new Rect2(left, bottom, right, top));
    }

    public static LayoutRegion[] Detect(
        IReadOnlyList<Segment2> segments,
        double tolerance = 1e-6)
    {
        Segment2[] horizontal = segments.Where(segment => segment.IsHorizontal(tolerance)).ToArray();
        Segment2[] vertical = segments.Where(segment => segment.IsVertical(tolerance)).ToArray();
        double[] xs = DistinctSorted(vertical.Select(segment => segment.Start.X), tolerance);
        double[] ys = DistinctSorted(horizontal.Select(segment => segment.Start.Y), tolerance);
        var cells = new List<LayoutRegion>();

        for (int xIndex = 0; xIndex + 1 < xs.Length; xIndex++)
        {
            for (int yIndex = 0; yIndex + 1 < ys.Length; yIndex++)
            {
                double left = xs[xIndex];
                double right = xs[xIndex + 1];
                double bottom = ys[yIndex];
                double top = ys[yIndex + 1];
                if (!HasVerticalBoundary(vertical, left, bottom, top, tolerance) ||
                    !HasVerticalBoundary(vertical, right, bottom, top, tolerance) ||
                    !HasHorizontalBoundary(horizontal, bottom, left, right, tolerance) ||
                    !HasHorizontalBoundary(horizontal, top, left, right, tolerance))
                {
                    continue;
                }

                cells.Add(new LayoutRegion(
                    $"table-cell-{cells.Count + 1}",
                    LayoutRegionKind.TableCell,
                    new Rect2(left, bottom, right, top)));
            }
        }

        return cells.ToArray();
    }

    private static bool HasVerticalBoundary(
        IEnumerable<Segment2> segments,
        double x,
        double bottom,
        double top,
        double tolerance) =>
        segments.Any(segment =>
            Math.Abs(segment.Start.X - x) <= tolerance &&
            segment.MinY <= bottom + tolerance &&
            segment.MaxY >= top - tolerance);

    private static bool HasHorizontalBoundary(
        IEnumerable<Segment2> segments,
        double y,
        double left,
        double right,
        double tolerance) =>
        segments.Any(segment =>
            Math.Abs(segment.Start.Y - y) <= tolerance &&
            segment.MinX <= left + tolerance &&
            segment.MaxX >= right - tolerance);

    private static double[] DistinctSorted(IEnumerable<double> values, double tolerance)
    {
        var result = new List<double>();
        foreach (double value in values.OrderBy(value => value))
        {
            if (result.Count == 0 || Math.Abs(value - result[^1]) > tolerance)
            {
                result.Add(value);
            }
        }

        return result.ToArray();
    }
}

public static class NoteColumnDetector
{
    public static LayoutRegion[] Detect(
        Rect2 frameBounds,
        IReadOnlyList<Segment2> separatorSegments,
        IReadOnlyList<Point2> textAnchors,
        double tolerance = 1e-6)
    {
        double[] separatorXs = separatorSegments
            .Where(segment => segment.IsVertical(tolerance))
            .Where(segment => segment.Start.X > frameBounds.Left + tolerance)
            .Where(segment => segment.Start.X < frameBounds.Right - tolerance)
            .Where(segment =>
                Math.Min(segment.MaxY, frameBounds.Top) -
                Math.Max(segment.MinY, frameBounds.Bottom) >= frameBounds.Height * 0.50)
            .Select(segment => segment.Start.X)
            .OrderBy(x => x)
            .Aggregate(
                new List<double>(),
                (values, x) =>
                {
                    if (values.Count == 0 || Math.Abs(values[^1] - x) > tolerance)
                    {
                        values.Add(x);
                    }

                    return values;
                })
            .ToArray();

        if (separatorXs.Length == 0)
        {
            separatorXs = DetectWhitespaceSeparators(frameBounds, textAnchors, tolerance);
        }

        double[] boundaries = [frameBounds.Left, .. separatorXs, frameBounds.Right];
        var columns = new List<LayoutRegion>();
        for (int index = 0; index + 1 < boundaries.Length; index++)
        {
            var bounds = new Rect2(
                boundaries[index],
                frameBounds.Bottom,
                boundaries[index + 1],
                frameBounds.Top);
            if (!textAnchors.Any(anchor =>
                    anchor.X >= bounds.Left - tolerance &&
                    anchor.X <= bounds.Right + tolerance &&
                    anchor.Y >= bounds.Bottom - tolerance &&
                    anchor.Y <= bounds.Top + tolerance))
            {
                continue;
            }

            columns.Add(new LayoutRegion(
                $"note-column-{columns.Count + 1}",
                LayoutRegionKind.NoteColumn,
                bounds));
        }

        return columns.ToArray();
    }

    private static double[] DetectWhitespaceSeparators(
        Rect2 frameBounds,
        IReadOnlyList<Point2> textAnchors,
        double tolerance)
    {
        double bandTolerance = Math.Max(tolerance * 4, frameBounds.Width * 0.02);
        Point2[] ordered = textAnchors
            .Where(anchor =>
                anchor.X >= frameBounds.Left - tolerance &&
                anchor.X <= frameBounds.Right + tolerance &&
                anchor.Y >= frameBounds.Bottom - tolerance &&
                anchor.Y <= frameBounds.Top + tolerance)
            .OrderBy(anchor => anchor.X)
            .ToArray();
        var bands = new List<List<Point2>>();
        foreach (Point2 anchor in ordered)
        {
            if (bands.Count == 0 ||
                anchor.X - bands[^1].Average(item => item.X) > bandTolerance)
            {
                bands.Add([]);
            }

            bands[^1].Add(anchor);
        }

        double[] stableStarts = bands
            .Where(band => band.Count >= 3)
            .Select(band => band.Average(anchor => anchor.X))
            .OrderBy(x => x)
            .ToArray();
        if (stableStarts.Length < 2)
        {
            return [];
        }

        double minimumColumnSeparation = Math.Max(bandTolerance * 3, frameBounds.Width * 0.12);
        var separators = new List<double>();
        for (int index = 0; index + 1 < stableStarts.Length; index++)
        {
            if (stableStarts[index + 1] - stableStarts[index] >= minimumColumnSeparation)
            {
                separators.Add((stableStarts[index] + stableStarts[index + 1]) / 2);
            }
        }

        return separators.ToArray();
    }
}

public sealed record NarrativeLayoutSample(
    Point2 Anchor,
    Rect2 Bounds,
    string Text,
    bool IsLeftAligned);

public static class NarrativeColumnDetector
{
    public static LayoutRegion[] Detect(
        IReadOnlyList<NarrativeLayoutSample> samples,
        double medianTextHeight,
        double tolerance = 1e-6)
    {
        NarrativeLayoutSample[] narrative = samples
            .Where(sample => sample.IsLeftAligned)
            .Where(sample => sample.Text.Trim().Length >= 40)
            .ToArray();
        if (narrative.Length < 12)
        {
            return [];
        }

        var frame = new Rect2(
            narrative.Min(sample => sample.Bounds.Left),
            narrative.Min(sample => sample.Bounds.Bottom),
            narrative.Max(sample => sample.Bounds.Right),
            narrative.Max(sample => sample.Bounds.Top));
        double height = Math.Max(medianTextHeight, tolerance);
        if (frame.Height < height * 12 ||
            frame.Width < height * 20 ||
            frame.Height > height * 220 ||
            frame.Width > height * 250)
        {
            return [];
        }

        LayoutRegion[] columns = NoteColumnDetector.Detect(
            frame,
            [],
            narrative.Select(sample => sample.Anchor).ToArray(),
            tolerance);
        if (columns.Length is < 2 or > 4 ||
            columns.Any(column => narrative.Count(sample =>
                sample.Anchor.X >= column.Bounds.Left - tolerance &&
                sample.Anchor.X <= column.Bounds.Right + tolerance &&
                sample.Anchor.Y >= column.Bounds.Bottom - tolerance &&
                sample.Anchor.Y <= column.Bounds.Top + tolerance) < 3))
        {
            return [];
        }

        return columns;
    }
}

public enum LayoutRiskLevel
{
    Low,
    Medium,
    High
}

public enum LayoutRiskCode
{
    Overflow,
    CrossRegion,
    AnchorDrift,
    TextOverlap,
    GeometryOverlap
}

public sealed record LayoutRiskInput(
    LayoutRiskCode Code,
    double SourceOverlapArea = 0,
    double CandidateOverlapArea = 0,
    double SmallerCandidateArea = 0);

public sealed record LayoutRisk(
    LayoutRiskCode Code,
    LayoutRiskLevel Level,
    double NewOverlapRatio);

public static class LayoutRiskClassifier
{
    public static LayoutRisk Classify(LayoutRiskInput input)
    {
        double newOverlap = Math.Max(0, input.CandidateOverlapArea - input.SourceOverlapArea);
        double ratio = input.SmallerCandidateArea <= 0
            ? 0
            : newOverlap / input.SmallerCandidateArea;
        LayoutRiskLevel level = input.Code is LayoutRiskCode.CrossRegion or LayoutRiskCode.AnchorDrift
            ? LayoutRiskLevel.High
            : ratio < 0.05
                ? LayoutRiskLevel.Low
                : ratio <= 0.15
                    ? LayoutRiskLevel.Medium
                    : LayoutRiskLevel.High;
        return new LayoutRisk(input.Code, level, ratio);
    }
}

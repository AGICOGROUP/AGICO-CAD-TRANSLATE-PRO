namespace CadTranslation.Core;

public sealed record NarrativeInlineItem(
    string Id,
    double DesiredWidth,
    double Height);

public sealed record NarrativeInlinePlacement(
    string Id,
    double XOffset,
    double YOffset,
    double Width,
    double Height);

public sealed record NarrativeInlinePacking(
    IReadOnlyList<NarrativeInlinePlacement> Placements,
    double Height,
    int LineCount);

public static class NarrativeInlinePacker
{
    public static double MeasurementSafeWidth(
        double desiredWidth,
        double safetyScale = 1.03)
    {
        if (desiredWidth <= 0 || safetyScale < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(desiredWidth));
        }

        return desiredWidth * safetyScale;
    }

    public static NarrativeInlinePacking Pack(
        IReadOnlyList<NarrativeInlineItem> items,
        double availableWidth,
        double horizontalGap,
        double verticalGap,
        double tolerance = 1e-6)
    {
        if (items.Count == 0)
        {
            return new NarrativeInlinePacking([], 0, 0);
        }

        if (availableWidth <= tolerance ||
            items.Any(item => item.DesiredWidth <= 0 || item.Height <= 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(items),
                "Inline packing requires positive dimensions.");
        }

        double x = 0;
        double y = 0;
        double lineHeight = 0;
        int lineCount = 1;
        var placements = new List<NarrativeInlinePlacement>(items.Count);
        foreach (NarrativeInlineItem item in items)
        {
            double width = Math.Min(availableWidth, item.DesiredWidth);
            if (x > tolerance &&
                x + width > availableWidth + tolerance)
            {
                y += lineHeight + Math.Max(0, verticalGap);
                x = 0;
                lineHeight = 0;
                lineCount++;
            }

            placements.Add(new NarrativeInlinePlacement(
                item.Id,
                x,
                y,
                width,
                item.Height));
            x += width + Math.Max(0, horizontalGap);
            lineHeight = Math.Max(lineHeight, item.Height);
        }

        return new NarrativeInlinePacking(
            placements,
            y + lineHeight,
            lineCount);
    }
}

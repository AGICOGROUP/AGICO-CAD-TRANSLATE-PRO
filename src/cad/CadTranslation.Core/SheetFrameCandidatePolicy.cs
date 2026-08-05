namespace CadTranslation.Core;

public static class SheetFrameCandidatePolicy
{
    public static bool IsCandidate(
        string layer,
        Rect2 bounds,
        double medianTextHeight,
        int containedTextCount)
    {
        if (string.IsNullOrWhiteSpace(layer) ||
            medianTextHeight <= 0 ||
            containedTextCount < 2)
        {
            return false;
        }

        bool frameLayer = layer.Contains("FRAME", StringComparison.OrdinalIgnoreCase) ||
            layer.Contains("图框", StringComparison.Ordinal);
        return frameLayer &&
            bounds.Width >= medianTextHeight * 30 &&
            bounds.Height >= medianTextHeight * 18;
    }
}

public static class SheetFrameBoundaryPolicy
{
    public static Rect2 Select(
        Rect2 blockExtents,
        IReadOnlyList<Rect2> detectedRectangles,
        double tolerance = 1e-6)
    {
        Rect2[] candidates = detectedRectangles
            .Where(rectangle => blockExtents.Contains(rectangle, tolerance))
            .Where(rectangle => rectangle.Width >= blockExtents.Width * 0.75)
            .Where(rectangle => rectangle.Height >= blockExtents.Height * 0.75)
            .OrderByDescending(rectangle => rectangle.Area)
            .ToArray();
        return candidates.Length == 0 ? blockExtents : candidates[0];
    }
}

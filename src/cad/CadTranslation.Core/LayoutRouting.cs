namespace CadTranslation.Core;

public enum LayoutHandlerKind
{
    TableCell,
    NoteColumn,
    Preserve
}

public static class LayoutRouter
{
    public static LayoutHandlerKind Select(LayoutRegionKind? regionKind, string text)
    {
        _ = text;
        return regionKind switch
        {
            LayoutRegionKind.TableCell => LayoutHandlerKind.TableCell,
            LayoutRegionKind.NoteColumn => LayoutHandlerKind.NoteColumn,
            _ => LayoutHandlerKind.Preserve
        };
    }
}

public enum TextAttachmentKind
{
    TopLeft,
    TopCenter,
    TopRight,
    MiddleLeft,
    MiddleCenter,
    MiddleRight,
    BottomLeft,
    BottomCenter,
    BottomRight
}

public static class TextAnchorPolicy
{
    public static TextAttachmentKind Map(string horizontalMode, string verticalMode)
    {
        int column = horizontalMode switch
        {
            "TextCenter" or "TextMid" or "TextAlign" or "TextFit" => 1,
            "TextRight" => 2,
            _ => 0
        };
        int row = verticalMode switch
        {
            "TextTop" => 0,
            "TextVerticalMid" => 1,
            _ => 2
        };
        return (TextAttachmentKind)(row * 3 + column);
    }

    public static bool IsPreserved(
        Point2 sourceAnchor,
        Point2 candidateAnchor,
        double originalTextHeight)
    {
        double deltaX = candidateAnchor.X - sourceAnchor.X;
        double deltaY = candidateAnchor.Y - sourceAnchor.Y;
        double distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        return distance <= Math.Max(1e-6, originalTextHeight * 0.15);
    }
}

public static class TableCellAnchorDriftPolicy
{
    public static LayoutRiskLevel Classify(bool candidateInsideCell) =>
        candidateInsideCell ? LayoutRiskLevel.Medium : LayoutRiskLevel.High;
}

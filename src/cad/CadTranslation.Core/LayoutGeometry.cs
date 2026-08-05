namespace CadTranslation.Core;

public readonly record struct Point2(double X, double Y);

public readonly record struct Segment2(Point2 Start, Point2 End)
{
    public double MinX => Math.Min(Start.X, End.X);
    public double MaxX => Math.Max(Start.X, End.X);
    public double MinY => Math.Min(Start.Y, End.Y);
    public double MaxY => Math.Max(Start.Y, End.Y);

    public bool IsHorizontal(double tolerance = 1e-6) =>
        Math.Abs(Start.Y - End.Y) <= tolerance && MaxX - MinX > tolerance;

    public bool IsVertical(double tolerance = 1e-6) =>
        Math.Abs(Start.X - End.X) <= tolerance && MaxY - MinY > tolerance;
}

public readonly record struct Rect2
{
    public Rect2(double x1, double y1, double x2, double y2)
    {
        Left = Math.Min(x1, x2);
        Bottom = Math.Min(y1, y2);
        Right = Math.Max(x1, x2);
        Top = Math.Max(y1, y2);
    }

    public double Left { get; }
    public double Bottom { get; }
    public double Right { get; }
    public double Top { get; }
    public double Width => Right - Left;
    public double Height => Top - Bottom;
    public double Area => Width * Height;
    public Point2 Center => new((Left + Right) / 2, (Bottom + Top) / 2);

    public bool Contains(Rect2 other, double tolerance = 1e-6) =>
        other.Left >= Left - tolerance &&
        other.Right <= Right + tolerance &&
        other.Bottom >= Bottom - tolerance &&
        other.Top <= Top + tolerance;
}

public readonly record struct Transform2(
    double M11,
    double M12,
    double M21,
    double M22,
    double TranslationX,
    double TranslationY)
{
    public static Transform2 Identity => new(1, 0, 0, 1, 0, 0);

    public static Transform2 Translation(double x, double y) =>
        new(1, 0, 0, 1, x, y);

    public static Transform2 FromScaleRotationTranslation(
        double scaleX,
        double scaleY,
        double rotationRadians,
        double translationX,
        double translationY)
    {
        double cosine = Math.Cos(rotationRadians);
        double sine = Math.Sin(rotationRadians);
        return new Transform2(
            cosine * scaleX,
            -sine * scaleY,
            sine * scaleX,
            cosine * scaleY,
            translationX,
            translationY);
    }

    public Point2 Apply(Point2 point) => new(
        M11 * point.X + M12 * point.Y + TranslationX,
        M21 * point.X + M22 * point.Y + TranslationY);

    public Transform2 Compose(Transform2 local) => new(
        M11 * local.M11 + M12 * local.M21,
        M11 * local.M12 + M12 * local.M22,
        M21 * local.M11 + M22 * local.M21,
        M21 * local.M12 + M22 * local.M22,
        M11 * local.TranslationX + M12 * local.TranslationY + TranslationX,
        M21 * local.TranslationX + M22 * local.TranslationY + TranslationY);

    public Rect2 Apply(Rect2 bounds)
    {
        Point2[] corners =
        [
            Apply(new Point2(bounds.Left, bounds.Bottom)),
            Apply(new Point2(bounds.Left, bounds.Top)),
            Apply(new Point2(bounds.Right, bounds.Bottom)),
            Apply(new Point2(bounds.Right, bounds.Top))
        ];
        return new Rect2(
            corners.Min(point => point.X),
            corners.Min(point => point.Y),
            corners.Max(point => point.X),
            corners.Max(point => point.Y));
    }
}

public static class TextBoundsEstimator
{
    public static (double Width, double Height) AvailableSizeFromAnchor(
        Rect2 allowed,
        Point2 anchor,
        TextAttachmentKind attachment)
    {
        double x = Math.Clamp(anchor.X, allowed.Left, allowed.Right);
        double y = Math.Clamp(anchor.Y, allowed.Bottom, allowed.Top);
        int column = (int)attachment % 3;
        int row = (int)attachment / 3;
        double width = column switch
        {
            1 => 2 * Math.Min(x - allowed.Left, allowed.Right - x),
            2 => x - allowed.Left,
            _ => allowed.Right - x
        };
        double height = row switch
        {
            0 => y - allowed.Bottom,
            1 => 2 * Math.Min(y - allowed.Bottom, allowed.Top - y),
            _ => allowed.Top - y
        };
        return (Math.Max(1e-6, width), Math.Max(1e-6, height));
    }

    public static Rect2 FromActualBox(
        Point2 anchor,
        double width,
        double height,
        TextAttachmentKind attachment,
        double rotationRadians)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        int column = (int)attachment % 3;
        int row = (int)attachment / 3;
        double left = column switch
        {
            1 => -width / 2,
            2 => -width,
            _ => 0
        };
        double bottom = row switch
        {
            0 => -height,
            1 => -height / 2,
            _ => 0
        };
        var local = new Rect2(left, bottom, left + width, bottom + height);
        Transform2 transform = Transform2.FromScaleRotationTranslation(
            1,
            1,
            rotationRadians,
            anchor.X,
            anchor.Y);
        return transform.Apply(local);
    }

    public static Rect2 Estimate(
        Point2 anchor,
        double textHeight,
        double widthFactor,
        string text,
        string horizontalMode,
        string verticalMode,
        double rotationRadians)
    {
        double height = Math.Max(textHeight, 1e-6);
        double width = Math.Max(
            height,
            LayoutTextMetrics.EstimateEmWidth(text) * height * Math.Max(widthFactor, 1e-6));
        double left = horizontalMode switch
        {
            "TextCenter" or "TextMid" or "TextAlign" or "TextFit" => -width / 2,
            "TextRight" => -width,
            _ => 0
        };
        double bottom = verticalMode switch
        {
            "TextTop" => -height,
            "TextVerticalMid" => -height / 2,
            "TextBottom" => 0,
            _ => -height * 0.20
        };
        var local = new Rect2(left, bottom, left + width, bottom + height * 1.20);
        Transform2 transform = Transform2.FromScaleRotationTranslation(
            1,
            1,
            rotationRadians,
            anchor.X,
            anchor.Y);
        return transform.Apply(local);
    }
}

using System.Text.RegularExpressions;

namespace CadTranslation.Core;

public enum LayoutAction
{
    Wrap,
    CompressWidth,
    ShrinkHeight
}

public sealed record LayoutFitRequest(
    double SourceWidth,
    double SourceHeight,
    double CandidateWidth,
    double CandidateHeight,
    double OriginalWidthFactor,
    double OriginalTextHeight,
    bool CanWrap,
    double WrappedWidth,
    double WrappedHeight);

public sealed record LayoutFitDecision(
    IReadOnlyList<LayoutAction> Actions,
    double WidthFactor,
    double TextHeight,
    bool NeedsTextReflow,
    bool ManualReview);

public static class LayoutFitPolicy
{
    public const double MinimumWidthScale = 0.70;
    public const double MinimumHeightScale = 0.55;
    public const double EmergencyMinimumHeightScale = 0.10;
    private const double Tolerance = 1.001;

    public static LayoutFitDecision Decide(LayoutFitRequest request)
    {
        Validate(request);
        var actions = new List<LayoutAction>();
        double width = request.CandidateWidth;
        double height = request.CandidateHeight;

        if (Fits(width, height, request.SourceWidth, request.SourceHeight))
            return new LayoutFitDecision(actions, request.OriginalWidthFactor, request.OriginalTextHeight, false, false);

        if (request.CanWrap && request.WrappedWidth > 0 && request.WrappedHeight > 0)
        {
            width = request.WrappedWidth;
            height = request.WrappedHeight;
            actions.Add(LayoutAction.Wrap);
        }

        double widthScale = Math.Clamp(request.SourceWidth / width, MinimumWidthScale, 1.0);
        if (widthScale < 1.0)
        {
            width *= widthScale;
            actions.Add(LayoutAction.CompressWidth);
        }

        double heightScale = Math.Clamp(
            Math.Min(request.SourceWidth / width, request.SourceHeight / height),
            MinimumHeightScale,
            1.0);
        if (heightScale < 1.0)
        {
            width *= heightScale;
            height *= heightScale;
            actions.Add(LayoutAction.ShrinkHeight);
        }

        bool unresolved = !Fits(width, height, request.SourceWidth, request.SourceHeight);
        return new LayoutFitDecision(
            actions,
            request.OriginalWidthFactor * widthScale,
            request.OriginalTextHeight * heightScale,
            unresolved,
            unresolved);
    }

    public static double ClampEmergencyHeightScale(double requestedScale)
    {
        if (requestedScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedScale));
        }

        return Math.Clamp(requestedScale, EmergencyMinimumHeightScale, 1.0);
    }

    public static double ClampReadableHeightScale(double requestedScale)
    {
        if (requestedScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedScale));
        }

        return Math.Clamp(requestedScale, MinimumHeightScale, 1.0);
    }

    public static bool NeedsContainmentRetry(
        bool aggregateFits,
        bool allItemsInside) =>
        !aggregateFits || !allItemsInside;

    public static double AuditCorrectionScale(
        double candidateWidth,
        double candidateHeight,
        double allowedWidth,
        double allowedHeight)
    {
        if (candidateWidth <= 0 || candidateHeight <= 0 ||
            allowedWidth <= 0 || allowedHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(candidateWidth));
        }

        double required = Math.Min(
            allowedWidth / candidateWidth,
            allowedHeight / candidateHeight);
        if (required >= 1)
        {
            return 1;
        }

        return Math.Clamp(
            required * 0.90,
            EmergencyMinimumHeightScale,
            1);
    }

    private static bool Fits(double width, double height, double sourceWidth, double sourceHeight) =>
        width <= sourceWidth * Tolerance && height <= sourceHeight * Tolerance;

    private static void Validate(LayoutFitRequest request)
    {
        if (request.SourceWidth <= 0 || request.SourceHeight <= 0 ||
            request.CandidateWidth <= 0 || request.CandidateHeight <= 0 ||
            request.OriginalWidthFactor <= 0 || request.OriginalTextHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Layout dimensions and original text factors must be positive.");
    }
}

public static class BilingualPlacementPolicy
{
    public static IReadOnlyList<double> HeightScales { get; } =
        [.45, .35, .30, .25, .20, .15, LayoutFitPolicy.EmergencyMinimumHeightScale];

    public static Rect2 PlaceAtCellBottom(Rect2 cell, double width, double height, double margin)
    {
        double left = Math.Clamp(cell.Center.X - width / 2, cell.Left + margin, cell.Right - margin - width);
        return new Rect2(left, cell.Bottom + margin, left + width, cell.Bottom + margin + height);
    }

    public static Rect2 AllowedForTightRotatedLabel(
        Rect2 container, Rect2 source, double textHeight, double rotation, bool isTableCell)
    {
        if (isTableCell || textHeight <= 0 || Math.Abs(Math.Sin(rotation)) < .25 ||
            source.Width > textHeight * 2 || source.Height > textHeight * 2)
            return container;

        double edgeDistance = new[] {
            source.Left - container.Left, container.Right - source.Right,
            source.Bottom - container.Bottom, container.Top - source.Top }.Min();
        if (edgeDistance > textHeight * 1.5) return container;

        double margin = textHeight * 4;
        return new Rect2(container.Left - margin, container.Bottom - margin,
            container.Right + margin, container.Top + margin);
    }

    public static IReadOnlyList<Rect2> EmergencyCandidates(Rect2 allowed, Rect2 source, double width, double height, double margin)
    {
        if (width <= 0 || height <= 0 || width + margin * 2 > allowed.Width || height + margin * 2 > allowed.Height)
            return Array.Empty<Rect2>();
        double[] xs = { source.Left, source.Center.X - width / 2, source.Right - width,
            allowed.Left + margin, allowed.Center.X - width / 2, allowed.Right - margin - width };
        double[] ys = { source.Bottom - margin - height, source.Top + margin,
            allowed.Bottom + margin, allowed.Center.Y - height / 2, allowed.Top - margin - height };
        return xs.SelectMany(x => ys.Select(y => new Rect2(
                Math.Clamp(x, allowed.Left + margin, allowed.Right - margin - width),
                Math.Clamp(y, allowed.Bottom + margin, allowed.Top - margin - height),
                Math.Clamp(x, allowed.Left + margin, allowed.Right - margin - width) + width,
                Math.Clamp(y, allowed.Bottom + margin, allowed.Top - margin - height) + height)))
            .Distinct()
            .OrderBy(r => Math.Pow(r.Center.X - source.Center.X, 2) + Math.Pow(r.Center.Y - source.Center.Y, 2))
            .ToArray();
    }

    public static IReadOnlyList<double> CandidateWidths(
        double allowedWidth,
        double sourceWidth,
        double textHeight,
        double unwrappedWidth)
    {
        if (allowedWidth <= 0 || sourceWidth <= 0 || textHeight <= 0 || unwrappedWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(allowedWidth));

        var widths = new List<double>();
        if (unwrappedWidth <= allowedWidth)
            widths.Add(unwrappedWidth);
        widths.Add(Math.Min(allowedWidth, Math.Max(sourceWidth, textHeight * 4)));
        widths.Add(Math.Min(allowedWidth, Math.Max(sourceWidth * 1.6, textHeight * 8)));
        return widths.Distinct().ToArray();
    }
}

public sealed record LayoutTextProfile(string ObjectType, string HorizontalMode, string Text);

public static partial class NarrativeTextClassifier
{
    public static bool IsConvertibleDbText(LayoutTextProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!string.Equals(profile.ObjectType, "AcDbText", StringComparison.Ordinal) ||
            !string.Equals(profile.HorizontalMode, "TextLeft", StringComparison.Ordinal))
            return false;

        string text = profile.Text?.Trim() ?? string.Empty;
        int wordCount = WordPattern().Matches(text).Count;
        return wordCount >= 6;
    }

    [GeneratedRegex(@"[A-Za-z]+(?:[-'][A-Za-z]+)*")]
    private static partial Regex WordPattern();

}

public static partial class FixedLabelTextClassifier
{
    public static bool ShouldWrap(LayoutTextProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return string.Equals(profile.ObjectType, "AcDbText", StringComparison.Ordinal) &&
               WordPattern().Matches(profile.Text?.Trim() ?? string.Empty).Count >= 6;
    }

    [GeneratedRegex(@"[A-Za-z]+(?:[-'][A-Za-z]+)*")]
    private static partial Regex WordPattern();
}

public static class LayoutTextMetrics
{
    public static double EstimateEmWidth(string? text)
    {
        double width = 0;
        foreach (char value in text ?? string.Empty)
        {
            width += value switch
            {
                >= '\u3400' and <= '\u9fff' => 1.0,
                >= '\uf900' and <= '\ufaff' => 1.0,
                ' ' or '\t' => 0.33,
                >= 'A' and <= 'Z' => 0.65,
                >= 'a' and <= 'z' => 0.55,
                >= '0' and <= '9' => 0.60,
                <= '\u007f' => 0.45,
                _ => 0.80
            };
        }
        return width;
    }
}

public static class LayoutCollision
{
    public static bool IsNewSevereOverlap(
        double sourceOverlapArea,
        double candidateOverlapArea,
        double smallerCandidateArea)
    {
        if (sourceOverlapArea < 0 || candidateOverlapArea < 0 || smallerCandidateArea <= 0)
            throw new ArgumentOutOfRangeException(nameof(smallerCandidateArea), "Overlap areas cannot be negative and candidate area must be positive.");

        double newOverlapArea = Math.Max(0, candidateOverlapArea - sourceOverlapArea);
        return newOverlapArea / smallerCandidateArea >= 0.10;
    }
}

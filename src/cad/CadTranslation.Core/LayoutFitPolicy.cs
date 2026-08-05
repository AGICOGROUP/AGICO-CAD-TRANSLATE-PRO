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

namespace CadTranslation.Core;

public static class MTextWrapWidthPolicy
{
    public static double Select(
        double currentWidth,
        double allowedWidth,
        double textHeight)
    {
        if (currentWidth < 0 || allowedWidth <= 0 || textHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(allowedWidth),
                "MText widths and text height must be positive.");
        }

        double minimum = Math.Min(textHeight, allowedWidth);
        double preferred = currentWidth > 0 ? currentWidth : allowedWidth;
        return Math.Clamp(preferred, minimum, allowedWidth);
    }
}

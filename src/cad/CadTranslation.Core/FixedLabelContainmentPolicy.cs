namespace CadTranslation.Core;

public sealed record FixedLabelMoveDecision(
    bool FitsAfterMove,
    double DeltaX,
    double DeltaY);

public static class FixedLabelContainmentPolicy
{
    public static FixedLabelMoveDecision Decide(
        Rect2 allowed,
        Rect2 candidate,
        double tolerance = 1e-6)
    {
        if (candidate.Width > allowed.Width + tolerance ||
            candidate.Height > allowed.Height + tolerance)
        {
            return new FixedLabelMoveDecision(false, 0, 0);
        }

        double deltaX = candidate.Left < allowed.Left
            ? allowed.Left - candidate.Left
            : candidate.Right > allowed.Right
                ? allowed.Right - candidate.Right
                : 0;
        double deltaY = candidate.Bottom < allowed.Bottom
            ? allowed.Bottom - candidate.Bottom
            : candidate.Top > allowed.Top
                ? allowed.Top - candidate.Top
                : 0;
        var moved = new Rect2(
            candidate.Left + deltaX,
            candidate.Bottom + deltaY,
            candidate.Right + deltaX,
            candidate.Top + deltaY);
        return new FixedLabelMoveDecision(
            allowed.Contains(moved, tolerance),
            deltaX,
            deltaY);
    }
}

public static class FixedLabelPaddingPolicy
{
    public static Rect2 Select(
        Rect2 allowed,
        Rect2 source,
        double proposedInset)
    {
        if (proposedInset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(proposedInset));
        }

        if (proposedInset * 2 >= allowed.Width ||
            proposedInset * 2 >= allowed.Height)
        {
            return allowed;
        }

        var inner = new Rect2(
            allowed.Left + proposedInset,
            allowed.Bottom + proposedInset,
            allowed.Right - proposedInset,
            allowed.Top - proposedInset);
        return inner.Contains(source) ? inner : allowed;
    }
}

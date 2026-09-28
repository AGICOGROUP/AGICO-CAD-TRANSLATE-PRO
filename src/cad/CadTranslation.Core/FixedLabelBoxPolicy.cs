namespace CadTranslation.Core;

/// <summary>
/// Bounds the box a fixed label may be fitted into.
/// </summary>
/// <remarks>
/// A fixed label is a line of an existing note paragraph or a drawing caption, not a cell
/// to be filled. Its neighbour-derived slot can span a whole sheet (measured: 48 434 units
/// for a 14 200-unit note line), and feeding that width to the fitter has two effects, both
/// observed on job replace-tujian23r-20260928b:
/// <list type="bullet">
/// <item>the DBText -> MText conversion takes the slot width as its wrap width, so the
/// MText's geometric box balloons far past its own text;</item>
/// <item>the containment step then translates that inflated box to fit the allowed
/// rectangle, which drags the text sideways by up to the box width (measured: -34 036 units
/// on the 基础底板 note line, -13 789 units on the 成品仓基础短柱平面布置图 caption).</item>
/// </list>
/// The text therefore leaves its column and lands on the drawing. Keeping the box anchored
/// on the label's own source footprint preserves the paragraph margin and leaves the
/// containment step nothing to drag, while still allowing the target language to wrap
/// sideways and grow downwards inside the free space that the slot already reserved.
/// </remarks>
public static class FixedLabelBoxPolicy
{
    /// <summary>Longest a label may grow sideways before it must wrap or shrink instead.</summary>
    public const double MaximumWidthGrowth = 1.60;

    /// <summary>How far the anchor may sit from the source left edge and still count as left aligned.</summary>
    public const double AnchorToleranceInTextHeights = 0.25;

    public static Rect2 Bound(Rect2 allowed, Rect2 source, Point2 anchor, double originalTextHeight)
    {
        double textHeight = Math.Max(originalTextHeight, 1e-6);
        double width = Math.Max(source.Width, Math.Min(allowed.Width, source.Width * MaximumWidthGrowth));
        bool leftAligned = Math.Abs(anchor.X - source.Left) <= textHeight * AnchorToleranceInTextHeights;
        double left = leftAligned ? source.Left : source.Center.X - width / 2;
        double right = left + width;
        if (right > allowed.Right)
        {
            right = allowed.Right;
            left = right - width;
        }

        if (left < allowed.Left)
        {
            left = allowed.Left;
            right = Math.Min(allowed.Right, left + width);
        }

        right = Math.Max(right, source.Right);
        if (leftAligned)
        {
            left = source.Left;
        }

        // The bottom edge must stay within reach of the line itself. A slot can reach
        // thousands of units below the line (measured: 4 036 units, the plan drawing under
        // the note block), and the fallback placement parks the text on that bottom edge -
        // which is how the last note line ended up 3 549 units below the paragraph.
        double lineHeight = Math.Max(source.Height, textHeight);
        double downward = Math.Max(lineHeight * (LayoutFitPolicy.MaximumWrappedHeightGrowth - 1), 0);
        double top = source.Top;
        double bottom = Math.Min(source.Bottom, Math.Max(source.Bottom - downward, allowed.Bottom));
        if (bottom >= top)
        {
            bottom = top - lineHeight;
        }

        return new Rect2(left, bottom, right, top);
    }
}

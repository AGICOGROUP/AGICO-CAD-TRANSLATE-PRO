using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

internal static class TableCellLayout
{
    internal static LayoutAdjustment? Apply(
        Database database,
        Transaction transaction,
        LayoutTargetSnapshot target,
        CadLayoutText topology,
        Rect2 allowedTextBox,
        string reasonPrefix = "table-cell",
        bool force = false,
        double? absoluteMinimumHeight = null)
    {
        DBObject value = transaction.GetObject(target.ObjectId, OpenMode.ForWrite, false);
        if (value is AttributeDefinition or AttributeReference)
            return FixedLabelLayout.Apply(database, transaction, target, topology, allowedTextBox);
        if (value is not DBText and not MText ||
            value is AttributeDefinition or AttributeReference)
        {
            return null;
        }

        double originalHeight = value is DBText dbSource ? dbSource.Height : ((MText)value).TextHeight;
        double originalWidthFactor = value is DBText dbWidth ? dbWidth.WidthFactor : 1;
        Rect2 inner = CadLayoutGeometry.Inset(
            allowedTextBox,
            topology.Source.OriginalTextHeight * 0.10);
        Bounds2d allowed = Bounds2d.From(inner);
        if (!force && value is Entity current &&
            CadLayoutGeometry.TryLayoutBounds(current) is Bounds2d currentBounds &&
            allowed.Contains(currentBounds))
        {
            return null;
        }

        string oldHandle = value.Handle.ToString();
        MText text;
        DBText? replaced = null;
        if (value is DBText dbText)
        {
            replaced = dbText;
            text = TextAnchorMapper.Replace(
                database,
                transaction,
                dbText,
                target.RestoredText,
                TextAnchorMapper.ToAttachment(
                    target.Manifest.Properties.HorizontalMode,
                    target.Manifest.Properties.VerticalMode),
                topology.Source.Anchor,
                inner.Width);
        }
        else
        {
            text = (MText)value;
            text.Width = MTextWrapWidthPolicy.Select(
                text.Width,
                inner.Width,
                text.TextHeight);
        }

        double maximumWrappedHeight = target.SourceBounds is Bounds2d sourceBox &&
            sourceBox.MaxY - sourceBox.MinY > 0
            ? (sourceBox.MaxY - sourceBox.MinY) * LayoutFitPolicy.MaximumWrappedHeightGrowth
            : double.PositiveInfinity;
        (double widthScale, double heightScale, bool fits) = Fit(
            text,
            target.RestoredText,
            allowed,
            originalHeight,
            absoluteMinimumHeight,
            maximumWrappedHeight);
        if (!fits)
        {
            ClampInside(text, allowed);
            fits = CadLayoutGeometry.TryFreshBounds(text) is Bounds2d clamped && allowed.Contains(clamped);
        }

        Bounds2d? candidate = CadLayoutGeometry.TryFreshBounds(text);
        if (replaced is not null)
        {
            replaced.Erase();
        }

        var actions = new List<string>();
        if (text.Width > 0) actions.Add("wrap");
        else actions.Add("keep-one-line");
        if (widthScale < 0.999) actions.Add("compress-width");
        if (heightScale < 0.999) actions.Add("shrink-height");
        if (!TextAnchorPolicy.IsPreserved(
                topology.Source.Anchor,
                new Point2(text.Location.X, text.Location.Y),
                originalHeight))
        {
            actions.Add("move-within-cell");
        }

        return new LayoutAdjustment(
            target.Manifest.RecordId,
            oldHandle,
            text.Handle.ToString(),
            "AcDbMText",
            actions,
            originalHeight,
            text.TextHeight,
            originalWidthFactor,
            widthScale,
            target.SourceBounds,
            candidate,
            !fits,
            fits ? $"{reasonPrefix}-fit" : $"{reasonPrefix}-readable-floor");
    }

    private static (double WidthScale, double HeightScale, bool Fits) Fit(
        MText text,
        string contents,
        Bounds2d allowed,
        double originalHeight,
        double? absoluteMinimumHeight,
        double maximumWrappedHeight)
    {
        foreach (double widthScale in CadLayoutGeometry.Steps(1, LayoutFitPolicy.MinimumWidthScale, 0.05))
        {
            text.Contents = CadLayoutGeometry.FormatWidth(contents, widthScale);
            text.TextHeight = originalHeight;
            if (TryMoveInside(text, allowed) && FitsHeightBudget(text, maximumWrappedHeight))
            {
                return (widthScale, 1, true);
            }
        }

        double minimumHeightScale = absoluteMinimumHeight is double floor
            ? LayoutFitPolicy.ClampReadableHeightScale(floor / originalHeight)
            : LayoutFitPolicy.MinimumHeightScale;
        foreach (double heightScale in CadLayoutGeometry.Steps(
                      0.95,
                      minimumHeightScale,
                      0.05))
        {
            text.Contents = CadLayoutGeometry.FormatWidth(contents, LayoutFitPolicy.MinimumWidthScale);
            text.TextHeight = originalHeight * heightScale;
            if (TryMoveInside(text, allowed) && FitsHeightBudget(text, maximumWrappedHeight))
            {
                return (LayoutFitPolicy.MinimumWidthScale, heightScale, true);
            }
        }

        // Wrapping would push the label past its height budget, which is how a long
        // target ends up on top of the table above it. Keep the original single line
        // and pay with a smaller glyph instead of invading the neighbouring cells.
        double singleLineWidth = text.Width;
        text.Width = 0;
        foreach (double heightScale in CadLayoutGeometry.Steps(
                      0.95,
                      LayoutFitPolicy.EmergencyMinimumHeightScale,
                      0.05))
        {
            text.Contents = CadLayoutGeometry.FormatWidth(contents, LayoutFitPolicy.MinimumWidthScale);
            text.TextHeight = originalHeight * heightScale;
            if (TryMoveInside(text, allowed) && FitsHeightBudget(text, maximumWrappedHeight))
            {
                return (LayoutFitPolicy.MinimumWidthScale, heightScale, true);
            }
        }

        text.Width = singleLineWidth;
        return (
            LayoutFitPolicy.MinimumWidthScale,
            minimumHeightScale,
            false);
    }

    private static bool FitsHeightBudget(MText text, double maximumHeight) =>
        !IsFinite(maximumHeight) ||
        CadLayoutGeometry.TryFreshBounds(text) is not Bounds2d bounds ||
        bounds.MaxY - bounds.MinY <= maximumHeight;

    private static void ClampInside(MText text, Bounds2d allowed)
    {
        TryMoveInside(text, allowed);
    }

    private static bool TryMoveInside(MText text, Bounds2d allowed)
    {
        if (CadLayoutGeometry.TryFreshBounds(text) is not Bounds2d bounds)
        {
            return false;
        }

        FixedLabelMoveDecision decision = FixedLabelContainmentPolicy.Decide(
            new Rect2(allowed.MinX, allowed.MinY, allowed.MaxX, allowed.MaxY),
            new Rect2(bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY));
        if (!decision.FitsAfterMove)
        {
            return false;
        }
        text.Location = new Point3d(
            text.Location.X + decision.DeltaX,
            text.Location.Y + decision.DeltaY,
            text.Location.Z);
        return CadLayoutGeometry.TryFreshBounds(text) is Bounds2d moved &&
               allowed.Contains(moved);
    }
}

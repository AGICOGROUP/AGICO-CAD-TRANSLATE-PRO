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

        (double widthScale, double heightScale, bool fits) = Fit(
            text,
            target.RestoredText,
            allowed,
            originalHeight,
            absoluteMinimumHeight);
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

        var actions = new List<string> { "wrap" };
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
        double? absoluteMinimumHeight)
    {
        foreach (double widthScale in CadLayoutGeometry.Steps(1, LayoutFitPolicy.MinimumWidthScale, 0.05))
        {
            text.Contents = CadLayoutGeometry.FormatWidth(contents, widthScale);
            text.TextHeight = originalHeight;
            if (TryMoveInside(text, allowed))
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
            if (TryMoveInside(text, allowed))
            {
                return (LayoutFitPolicy.MinimumWidthScale, heightScale, true);
            }
        }

        return (
            LayoutFitPolicy.MinimumWidthScale,
            minimumHeightScale,
            false);
    }

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

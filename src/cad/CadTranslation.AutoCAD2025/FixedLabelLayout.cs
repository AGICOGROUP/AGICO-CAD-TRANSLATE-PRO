using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

internal static class FixedLabelLayout
{
    internal static LayoutAdjustment? Apply(
        Database database,
        Transaction transaction,
        LayoutTargetSnapshot target,
        CadLayoutText topology,
        Rect2 allowedTextBox)
    {
        DBObject value = transaction.GetObject(target.ObjectId, OpenMode.ForWrite, false);
        if (value is MText)
        {
            return TableCellLayout.Apply(
                database,
                transaction,
                target,
                topology,
                allowedTextBox,
                "fixed-label");
        }


        if (value is DBText dbTextToWrap &&
            value is not AttributeDefinition and not AttributeReference &&
            FixedLabelTextClassifier.ShouldWrap(
                new LayoutTextProfile(
                    "AcDbText",
                    target.Manifest.Properties.HorizontalMode,
                    target.RestoredText)))
        {
            return TableCellLayout.Apply(
                database,
                transaction,
                target,
                topology,
                allowedTextBox,
                "fixed-label",
                force: true);
        }

        if (value is not DBText text ||
            !FixedLabelInPlaceScalePolicy.Allows(value.GetRXClass().Name))
        {
            return null;
        }

        double originalHeight = text.Height;
        double originalWidthFactor = text.WidthFactor;
        Rect2 inner = FixedLabelPaddingPolicy.Select(
            allowedTextBox,
            topology.Source.Bounds,
            topology.Source.OriginalTextHeight * 0.05);
        Bounds2d allowed = Bounds2d.From(inner);
        if (CadLayoutGeometry.TryBounds(text) is Bounds2d current &&
            allowed.Contains(current))
        {
            return null;
        }

        bool fits = false;
        bool moved = false;
        double widthScale = 1;
        double heightScale = 1;
        foreach (double scale in CadLayoutGeometry.Steps(
                     1,
                     LayoutFitPolicy.MinimumWidthScale,
                     0.05))
        {
            text.WidthFactor = originalWidthFactor * scale;
            text.Height = originalHeight;
            if (CadLayoutGeometry.TryBounds(text) is Bounds2d bounds &&
                allowed.Contains(bounds))
            {
                widthScale = scale;
                fits = true;
                break;
            }
        }

        if (!fits)
        {
            widthScale = LayoutFitPolicy.MinimumWidthScale;
            text.WidthFactor = originalWidthFactor * widthScale;
            foreach (double scale in CadLayoutGeometry.Steps(
                         0.95,
                         LayoutFitPolicy.MinimumHeightScale,
                         0.05))
            {
                text.Height = originalHeight * scale;
                if (CadLayoutGeometry.TryBounds(text) is Bounds2d bounds &&
                    allowed.Contains(bounds))
                {
                    heightScale = scale;
                    fits = true;
                    break;
                }
            }
        }

        if (!fits &&
            CadLayoutGeometry.TryBounds(text) is Bounds2d outside)
        {
            heightScale = LayoutFitPolicy.MinimumHeightScale;
            text.Height = originalHeight * heightScale;
            outside = CadLayoutGeometry.TryBounds(text) ?? outside;
            moved = TryMoveInside(text, allowed, outside);
            fits = moved;
        }

        Bounds2d? candidate = CadLayoutGeometry.TryBounds(text);
        if (candidate is null)
        {
            text.Height = originalHeight;
            text.WidthFactor = originalWidthFactor;
            return null;
        }

        var actions = new List<string>();
        if (widthScale < 0.999) actions.Add("compress-width");
        if (heightScale < 0.999) actions.Add("shrink-height");
        if (moved) actions.Add("move-within-slot");
        return new LayoutAdjustment(
            target.Manifest.RecordId,
            text.Handle.ToString(),
            text.Handle.ToString(),
            "AcDbText",
            actions,
            originalHeight,
            text.Height,
            originalWidthFactor,
            text.WidthFactor,
            target.SourceBounds,
            candidate,
            !fits,
            fits ? "fixed-label-fit" : "fixed-label-readable-floor");
    }

    private static bool TryMoveInside(DBText text, Bounds2d allowed, Bounds2d bounds)
    {
        FixedLabelMoveDecision decision = FixedLabelContainmentPolicy.Decide(
            new Rect2(allowed.MinX, allowed.MinY, allowed.MaxX, allowed.MaxY),
            new Rect2(bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY));
        if (!decision.FitsAfterMove)
        {
            return false;
        }

        if (Math.Abs(decision.DeltaX) > 1e-9 || Math.Abs(decision.DeltaY) > 1e-9)
        {
            text.TransformBy(Matrix3d.Displacement(
                new Vector3d(decision.DeltaX, decision.DeltaY, 0)));
        }
        return CadLayoutGeometry.TryBounds(text) is Bounds2d shifted &&
               allowed.Contains(shifted);
    }
}

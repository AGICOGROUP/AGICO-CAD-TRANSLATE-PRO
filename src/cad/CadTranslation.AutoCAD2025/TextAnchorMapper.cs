using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

internal static class TextAnchorMapper
{
    internal static AttachmentPoint ToAttachment(string horizontalMode, string verticalMode) =>
        TextAnchorPolicy.Map(horizontalMode, verticalMode) switch
        {
            TextAttachmentKind.TopLeft => AttachmentPoint.TopLeft,
            TextAttachmentKind.TopCenter => AttachmentPoint.TopCenter,
            TextAttachmentKind.TopRight => AttachmentPoint.TopRight,
            TextAttachmentKind.MiddleLeft => AttachmentPoint.MiddleLeft,
            TextAttachmentKind.MiddleCenter => AttachmentPoint.MiddleCenter,
            TextAttachmentKind.MiddleRight => AttachmentPoint.MiddleRight,
            TextAttachmentKind.BottomLeft => AttachmentPoint.BottomLeft,
            TextAttachmentKind.BottomCenter => AttachmentPoint.BottomCenter,
            TextAttachmentKind.BottomRight => AttachmentPoint.BottomRight,
            _ => AttachmentPoint.TopLeft
        };

    internal static MText Replace(
        Database database,
        Transaction transaction,
        DBText source,
        string contents,
        AttachmentPoint attachment,
        Point2 anchor,
        double width)
    {
        var owner = (BlockTableRecord)transaction.GetObject(source.OwnerId, OpenMode.ForWrite, false);
        var replacement = new MText();
        replacement.SetDatabaseDefaults(database);
        replacement.Contents = contents;
        replacement.Attachment = attachment;
        replacement.Location = new Point3d(anchor.X, anchor.Y, source.Position.Z);
        replacement.Width = Math.Max(source.Height, width);
        replacement.TextHeight = source.Height;
        replacement.TextStyleId = source.TextStyleId;
        replacement.LayerId = source.LayerId;
        replacement.Color = source.Color;
        replacement.LineWeight = source.LineWeight;
        replacement.LinetypeId = source.LinetypeId;
        replacement.LinetypeScale = source.LinetypeScale;
        // AutoCAD derives the MText direction from its normal. Assigning the
        // normal after Rotation resets rotated DBText replacements to 0 radians.
        // Establish the plane first, then restore the source rotation.
        replacement.Normal = source.Normal;
        replacement.Rotation = source.Rotation;
        owner.AppendEntity(replacement);
        transaction.AddNewlyCreatedDBObject(replacement, true);
        return replacement;
    }
}

internal static class CadLayoutGeometry
{
    internal const double MTextMeasurementSafetyScale = 1.02;

    internal static Bounds2d? TryLayoutBounds(Entity entity) =>
        entity is MText mText
            ? TryFreshBounds(mText)
            : TryBounds(entity);

    internal static Bounds2d? TryFreshBounds(MText text)
    {
        try
        {
            double width = Math.Max(text.TextHeight, text.ActualWidth) * MTextMeasurementSafetyScale;
            double height = Math.Max(text.TextHeight, text.ActualHeight) * MTextMeasurementSafetyScale;
            Rect2 bounds = TextBoundsEstimator.FromActualBox(
                new Point2(text.Location.X, text.Location.Y),
                width,
                height,
                ToAttachment(text.Attachment),
                text.Rotation);
            return Bounds2d.From(bounds);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return TryBounds(text);
        }
    }

    internal static Bounds2d? TryBounds(Entity entity)
    {
        try
        {
            Extents3d extents = entity.GeometricExtents;
            return new Bounds2d(
                extents.MinPoint.X,
                extents.MinPoint.Y,
                extents.MaxPoint.X,
                extents.MaxPoint.Y);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return null;
        }
    }

    internal static Rect2 Inset(Rect2 bounds, double inset)
    {
        double safeInset = Math.Min(
            Math.Max(0, inset),
            Math.Max(0, Math.Min(bounds.Width, bounds.Height) * 0.20));
        return new Rect2(
            bounds.Left + safeInset,
            bounds.Bottom + safeInset,
            bounds.Right - safeInset,
            bounds.Top - safeInset);
    }

    internal static string FormatWidth(string value, double widthScale) =>
        widthScale >= 0.999
            ? value
            : $"{{\\W{widthScale.ToString("0.###", CultureInfo.InvariantCulture)};{value}}}";

    internal static IEnumerable<double> Steps(double start, double floor, double step)
    {
        for (double value = start; value > floor + 1e-9; value -= step)
        {
            yield return Math.Round(value, 6);
        }

        yield return floor;
    }

    internal static double ActualHeight(MText text)
    {
        try
        {
            return Math.Max(text.TextHeight, text.ActualHeight);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return text.TextHeight;
        }
    }

    private static TextAttachmentKind ToAttachment(AttachmentPoint value) => value switch
    {
        AttachmentPoint.TopLeft => TextAttachmentKind.TopLeft,
        AttachmentPoint.TopCenter => TextAttachmentKind.TopCenter,
        AttachmentPoint.TopRight => TextAttachmentKind.TopRight,
        AttachmentPoint.MiddleLeft => TextAttachmentKind.MiddleLeft,
        AttachmentPoint.MiddleCenter => TextAttachmentKind.MiddleCenter,
        AttachmentPoint.MiddleRight => TextAttachmentKind.MiddleRight,
        AttachmentPoint.BottomLeft => TextAttachmentKind.BottomLeft,
        AttachmentPoint.BottomCenter => TextAttachmentKind.BottomCenter,
        AttachmentPoint.BottomRight => TextAttachmentKind.BottomRight,
        _ => TextAttachmentKind.TopLeft
    };
}

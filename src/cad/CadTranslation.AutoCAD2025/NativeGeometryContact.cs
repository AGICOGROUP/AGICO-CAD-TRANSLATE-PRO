using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

/// <summary>Contact with actual curve strokes; null means bounds-only/unsupported, not clear space.</summary>
internal static class NativeGeometryContact
{
    internal static bool BoundsOverlap(Rect2 a, Rect2 b) =>
        a.Right >= b.Left && a.Left <= b.Right && a.Top >= b.Bottom && a.Bottom <= b.Top;

    internal static bool? Intersects(Entity entity, Rect2 box)
    {
        try
        {
            var ext = entity.GeometricExtents;
            if (!BoundsOverlap(box, new Rect2(ext.MinPoint.X, ext.MinPoint.Y, ext.MaxPoint.X, ext.MaxPoint.Y))) return false;
            if (entity is Curve curve)
            {
                if (Math.Abs(ext.MaxPoint.Z - ext.MinPoint.Z) > 1e-6) return null;
                if (curve is Polyline wide && Enumerable.Range(0, wide.NumberOfVertices)
                    .Any(i => wide.GetStartWidthAt(i) > 0 || wide.GetEndWidthAt(i) > 0)) return null;
                if (curve is Polyline2d) return null; // Legacy widths are not center-line strokes.
                using var border = new Polyline(4);
                border.AddVertexAt(0, new Point2d(box.Left, box.Bottom), 0, 0, 0);
                border.AddVertexAt(1, new Point2d(box.Right, box.Bottom), 0, 0, 0);
                border.AddVertexAt(2, new Point2d(box.Right, box.Top), 0, 0, 0);
                border.AddVertexAt(3, new Point2d(box.Left, box.Top), 0, 0, 0);
                border.Closed = true;
                border.Elevation = ext.MinPoint.Z;
                var points = new Point3dCollection();
                curve.IntersectWith(border, Intersect.OnBothOperands, points, IntPtr.Zero, IntPtr.Zero);
                Point3d start = curve.StartPoint;
                return points.Count > 0 || (start.X >= box.Left && start.X <= box.Right && start.Y >= box.Bottom && start.Y <= box.Top);
            }
            if (entity is Dimension)
            {
                var parts = new DBObjectCollection();
                try
                {
                    entity.Explode(parts);
                    bool unknown = parts.Count == 0;
                    foreach (DBObject part in parts)
                    {
                        bool? contact = part is Entity child ? Intersects(child, box) : null;
                        if (contact == true) return true;
                        unknown |= contact is null;
                    }
                    return unknown ? null : false;
                }
                finally { foreach (DBObject part in parts) part.Dispose(); }
            }
            // Hatch holes/patterns, solids, proxy objects and nested blocks are not
            // reduced to imaginary filled rectangles. Keep their uncertainty visible.
            return null;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return null; }
    }
}

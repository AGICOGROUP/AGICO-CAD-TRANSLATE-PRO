using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

/// <summary>One bounded, text-only pass. Unsupported/nested objects remain for review.</summary>
internal static class ReplaceLocalCorrection
{
    internal static LayoutAdjustment[] Apply(Database db, CadLayoutBaseline baseline, LayoutAuditReport audit)
    {
        var originals = baseline.Definitions.SelectMany(d => d.Texts).ToDictionary(t => t.RecordId);
        var occupied = audit.Texts.ToDictionary(t => t.RecordId, t => t.CandidateBounds);
        var rows = audit.Texts.ToDictionary(t => t.RecordId);
        string[] selected = ReplaceLocalPlacement.Select(audit.ManualReview
            .Select(r => (r.RecordId, r.OtherRecordId, r.Code, r.Level)),
            originals.Values.Where(t => t.IsChanged).Select(t => t.RecordId));
        var corrections = new List<LayoutAdjustment>();
        foreach (string id in selected)
        {
            var source = originals[id];
            if (!source.DefinitionName.StartsWith("*Model_Space", StringComparison.Ordinal) &&
                !source.DefinitionName.StartsWith("*Paper_Space", StringComparison.Ordinal)) continue;
            var definition = baseline.Definitions.Single(d => d.Name == source.DefinitionName);
            double h = source.Source.OriginalTextHeight;
            Rect2 originalBox = rows[id].CandidateBounds;
            Rect2 allowed = source.Region is { Kind: LayoutRegionKind.TableCell or LayoutRegionKind.TitleBlock } region
                ? region.Bounds : definition.Regions.Where(r => r.Kind == LayoutRegionKind.ClosedFrame && r.Bounds.Contains(source.Source.Bounds))
                    .OrderBy(r => r.Bounds.Area).Select(r => r.Bounds).FirstOrDefault(
                        new Rect2(originalBox.Left - 6*h, originalBox.Bottom - 6*h, originalBox.Right + 6*h, originalBox.Top + 6*h));
            using var tx = db.TransactionManager.StartTransaction();
            var entity = (Entity)tx.GetObject(db.GetObjectId(false, new Handle(long.Parse(rows[id].NewHandle, NumberStyles.HexNumber)), 0), OpenMode.ForWrite);
            if (entity is not MText && (entity is not DBText || entity is AttributeDefinition or AttributeReference)) continue;
            double rotation = entity is MText mt ? mt.Rotation : ((DBText)entity).Rotation;
            Vector3d normal = entity is MText mp ? mp.Normal : ((DBText)entity).Normal;
            if (Math.Abs(rotation) > 1e-6 || !normal.IsEqualTo(Vector3d.ZAxis)) continue;
            using var saved = (Entity)entity.Clone();
            double initialHeight = entity is MText m ? m.TextHeight : ((DBText)entity).Height;
            double initialWidth = entity is MText mw ? mw.Width : ((DBText)entity).WidthFactor;
            var owner = (BlockTableRecord)tx.GetObject(entity.OwnerId, OpenMode.ForRead);
            var obstacles = owner.Cast<ObjectId>().Where(oid => oid != entity.ObjectId)
                .Select(oid => tx.GetObject(oid, OpenMode.ForRead)).OfType<Entity>()
                .Where(e => e is not DBText and not MText && !e.IsErased).ToArray();
            bool safe(Rect2 box)
            {
                if (!allowed.Contains(box)) return false;
                foreach (var other in definition.Texts.Where(t => t.RecordId != id))
                    if (Area(box, occupied[other.RecordId]) > Area(source.Source.Bounds, other.Source.Bounds) + 1e-6) return false;
                foreach (Entity obstacle in obstacles)
                {
                    bool? contact = NativeGeometryContact.Intersects(obstacle, box);
                    if (contact == false) continue;
                    // Unknown geometry is never treated as newly available whitespace.
                    if (CadLayoutGeometry.TryBounds(obstacle) is not { } b) return false;
                    var bounds = new Rect2(b.MinX, b.MinY, b.MaxX, b.MaxY);
                    if (!NativeGeometryContact.BoundsOverlap(source.Source.Bounds, bounds) ||
                        Area(box, bounds) > Area(source.Source.Bounds, bounds) + 1e-6) return false;
                    if (contact == true && NativeGeometryContact.Intersects(obstacle, source.Source.Bounds) != true) return false;
                }
                return true;
            }
            bool placed = false;
            // Preserve current readable size first. MText may use genuine horizontal
            // whitespace before the final bounded shrink attempts.
            foreach (double scale in new[] {1.0, .85, .70})
            {
                double height = Math.Max(initialHeight * scale, Math.Min(initialHeight, h * .55));
                foreach (double widthScale in entity is MText ? new[] {1.0, 1.6, 2.2} : new[] {1.0})
                {
                    entity.CopyFrom(saved);
                    if (entity is MText text) { text.TextHeight = height; if (initialWidth > 0) text.Width = initialWidth * widthScale; }
                    else ((DBText)entity).Height = height;
                    if (CadLayoutGeometry.TryBounds(entity) is not { } measured) continue;
                    var current = new Rect2(measured.MinX, measured.MinY, measured.MaxX, measured.MaxY);
                    foreach (Rect2 proposal in ReplaceLocalPlacement.Candidates(originalBox, current.Width, current.Height, h, allowed))
                    {
                        var delta = new Vector3d(proposal.Left-current.Left, proposal.Bottom-current.Bottom, 0);
                        entity.TransformBy(Matrix3d.Displacement(delta));
                        if (CadLayoutGeometry.TryBounds(entity) is { } actual && safe(new Rect2(actual.MinX, actual.MinY, actual.MaxX, actual.MaxY)))
                        {
                            occupied[id] = new Rect2(actual.MinX, actual.MinY, actual.MaxX, actual.MaxY);
                            corrections.Add(new LayoutAdjustment(id, rows[id].OldHandle, rows[id].NewHandle, entity.GetRXClass().Name,
                                new[] {"local-collision-correction"}, h, height, source.ObjectType == "AcDbText" ? initialWidth : 1,
                                source.ObjectType == "AcDbText" ? initialWidth : 1, Bounds2d.From(source.Source.Bounds), actual, false, "bounded-local-whitespace"));
                            placed = true; break;
                        }
                        entity.TransformBy(Matrix3d.Displacement(-delta));
                    }
                    if (placed) break;
                }
                if (placed) break;
            }
            // Side databases may have undo disabled: transaction abort alone does
            // not reliably restore MText properties after CopyFrom/trial fitting.
            if (!placed) entity.CopyFrom(saved);
            tx.Commit();
        }
        return corrections.ToArray();
    }

    private static double Area(Rect2 a, Rect2 b) => Math.Max(0, Math.Min(a.Right,b.Right)-Math.Max(a.Left,b.Left)) *
        Math.Max(0, Math.Min(a.Top,b.Top)-Math.Max(a.Bottom,b.Bottom));
}

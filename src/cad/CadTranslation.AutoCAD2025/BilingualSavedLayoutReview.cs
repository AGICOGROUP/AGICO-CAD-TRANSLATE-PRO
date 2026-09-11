using CadTranslation.Contracts;
using CadTranslation.Core;
using Autodesk.AutoCAD.DatabaseServices;
using System.Globalization;

namespace CadTranslation.AutoCAD2025;

internal static class BilingualSavedLayoutReview
{
    internal sealed record Risk(string Code, string RecordId, string SourceHandle, string TargetHandle,
        string? OtherHandle, string OwnerPath, Rect2? Bounds, string Severity = "medium");

    internal static Risk[] MeasureAndInspect(Database db, IReadOnlyList<BilingualDrawingImporter.Pair> pairs,
        IReadOnlyList<ManifestRecord> candidate, IReadOnlyDictionary<string, Rect2> allowed, List<CadObjectAccess.Issue> issues)
    {
        // Exchange records intentionally omit extents. Measure in the candidate's
        // already-open database; do not change its manifest or source-preservation comparison.
        using var tx = db.TransactionManager.StartTransaction();
        var access = new CadObjectAccess(db, tx, issues);
        var handles = pairs.Where(p=>p.Decision=="added").Select(p=>p.TargetHandle).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var owners = candidate.Where(r=>handles.Contains(r.Handle)).Select(r=>r.OwnerPath).ToHashSet();
        var measured = candidate.Select(row => {
            if (!owners.Contains(row.OwnerPath) || !long.TryParse(row.Handle,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out long value)) return row;
            ObjectId id;
            try { id = db.GetObjectId(false,new Handle(value),0); }
            catch (Autodesk.AutoCAD.Runtime.Exception) { return row; }
            Entity? entity = access.Read<Entity>(id,"saved-bilingual-bounds",recordId:row.RecordId);
            if (entity is null) return row;
            var b = CadLayoutGeometry.TryBounds(entity);
            if (entity is MText text && CadLayoutGeometry.TryFreshBounds(text) is { } fresh)
                b = b is null || !handles.Contains(row.Handle) ? fresh : new Bounds2d(Math.Min(b.MinX,fresh.MinX),Math.Min(b.MinY,fresh.MinY),Math.Max(b.MaxX,fresh.MaxX),Math.Max(b.MaxY,fresh.MaxY));
            if (b is null) return row;
            return row with { Geometry = row.Geometry with { Extents = new(new(b.MinX,b.MinY,0),new(b.MaxX,b.MaxY,0)) } };
        }).ToArray();
        return Inspect(pairs,measured,allowed);
    }

    internal static Risk[] Inspect(IReadOnlyList<BilingualDrawingImporter.Pair> pairs,
        IReadOnlyList<ManifestRecord> candidate, IReadOnlyDictionary<string, Rect2> allowed)
    {
        var byHandle = candidate.ToDictionary(r => r.Handle, StringComparer.OrdinalIgnoreCase);
        var byOwner = candidate.GroupBy(r => r.OwnerPath).ToDictionary(g => g.Key,
            g => g.Select(r => (Row: r, Bounds: Bounds(r))).Where(t => t.Bounds is not null).ToArray());
        var risks = new List<Risk>();
        // Reuse the reopened candidate. No extra CAD process or full
        // topology rebuild; bounding-box findings remain pointers for visual review.
        foreach (var pair in pairs.Where(p => p.Decision == "added").DistinctBy(p => p.TargetHandle))
        {
            if (!byHandle.TryGetValue(pair.TargetHandle, out var target)) continue; // Existing coverage check owns this failure.
            Rect2? box = Bounds(target);
            void Add(string code, string? other = null) => risks.Add(new(code, pair.RecordId,
                pair.SourceHandle, pair.TargetHandle, other, target.OwnerPath, box));
            if (box is not Rect2 actual) { Add("missing-saved-bounds"); continue; }
            double tolerance = Math.Max(1e-6, target.Properties.Height * .03);
            if (allowed.TryGetValue(pair.RecordId, out var region) && !region.Contains(actual, tolerance))
                Add("outside-placement-region");
            if (!pair.Bounds.Contains(actual, tolerance)) Add("saved-bounds-expanded");
            if (pair.HeightScale < .35) Add("small-target-text");
            foreach (var other in byOwner[target.OwnerPath])
            {
                if (other.Row.Handle.Equals(pair.TargetHandle, StringComparison.OrdinalIgnoreCase)) continue;
                Rect2 b = other.Bounds!.Value;
                double area = Math.Max(0, Math.Min(actual.Right,b.Right)-Math.Max(actual.Left,b.Left)) *
                    Math.Max(0, Math.Min(actual.Top,b.Top)-Math.Max(actual.Bottom,b.Bottom));
                if (area > 1e-9 && area / Math.Min(actual.Area,b.Area) >= .10)
                    Add("saved-text-overlap", other.Row.Handle);
            }
        }
        return risks.ToArray();
    }

    private static Rect2? Bounds(ManifestRecord row)
    {
        if (row.Geometry.Extents is not { } e) return null;
        var box = new Rect2(e.Minimum.X,e.Minimum.Y,e.Maximum.X,e.Maximum.Y);
        return double.IsFinite(box.Area) && box.Width > 0 && box.Height > 0 ? box : null;
    }
}

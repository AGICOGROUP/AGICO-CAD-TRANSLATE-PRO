using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadTranslation.Contracts;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

// Additive placement decides from the source rows known at import time. A long
// English line can still land on text that only becomes measurable once the
// candidate is written: dimension figures are the common case, because their
// display box is estimated from the dimstyle instead of read back.
//
// Measurement and mutation are deliberately split:
//   Plan   reads the reopened candidate (the same safe session the saved-layout
//          review uses) and works out where each colliding addition should go.
//   Apply  touches only our own MTexts in the working database. Reading a
//          Dimension's bounds there regenerates its display block and can abort
//          the console with no managed traceback, so the working pass never
//          measures anything but the text it is moving.
internal static class BilingualCollisionCorrection
{
    internal sealed record Correction(string RecordId, string TargetHandle, Rect2 Previous, Rect2 Planned,
        double Factor, double OffsetX, double OffsetY, string Method);

    // Same threshold the saved-layout review reports at: below this a touch is a
    // tight neighbour, above it the review calls it an overlap.
    private const double MinimumOverlapRatio = 0.10;

    internal static Correction[] Plan(Database inspect, IReadOnlyList<BilingualDrawingImporter.Pair> pairs,
        IReadOnlyList<ManifestRecord> rows, IEnumerable<CadLayoutText> topology,
        IReadOnlyDictionary<string, Rect2> allowed, List<CadObjectAccess.Issue> issues)
    {
        try
        {
            return PlanCore(inspect, pairs, rows, topology, allowed, issues);
        }
        catch (System.Exception exception)
        {
            issues.Add(new CadObjectAccess.Issue("collision-plan", null, null, null,
                "BilingualCollisionCorrection", exception.Message, null, exception.ToString()));
            return [];
        }
    }

    private static Correction[] PlanCore(Database inspect, IReadOnlyList<BilingualDrawingImporter.Pair> pairs,
        IReadOnlyList<ManifestRecord> rows, IEnumerable<CadLayoutText> topology,
        IReadOnlyDictionary<string, Rect2> allowed, List<CadObjectAccess.Issue> issues)
    {
        using var tx = inspect.TransactionManager.StartTransaction();
        var access = new CadObjectAccess(inspect, tx, issues);
        // Dimension boxes come from the plan-time estimate, widened, instead of being
        // read back: asking AutoCAD for a dimension's bounds generates its anonymous
        // display block and can end the console session outright.
        var estimates = new Dictionary<string, Rect2>(StringComparer.OrdinalIgnoreCase);
        foreach (CadLayoutText text in topology)
        {
            if (!text.ObjectType.Contains("Dimension", StringComparison.OrdinalIgnoreCase)) continue;
            Rect2 box = Inflate(text.Source.Bounds, 1.6);
            estimates[text.EntityHandle] = box;
        }
        ManifestRecord[] measured = Measure(rows, inspect, access, pairs, estimates);
        var byHandle = measured.ToDictionary(r => r.Handle, StringComparer.OrdinalIgnoreCase);
        var byOwner = measured.GroupBy(r => r.OwnerPath).ToDictionary(g => g.Key,
            g => g.Select(r => (Row: r, Box: Box(r))).Where(t => t.Box is not null).ToArray());

        var corrections = new List<Correction>();
        foreach (BilingualDrawingImporter.Pair pair in pairs.Where(p => p.Decision == "added")
                     .DistinctBy(p => p.TargetHandle, StringComparer.OrdinalIgnoreCase))
        {
            if (!byHandle.TryGetValue(pair.TargetHandle, out ManifestRecord? target)) continue;
            if (Box(target) is not Rect2 box || !byOwner.TryGetValue(target.OwnerPath, out var owner)) continue;
            double height = Math.Max(target.Properties.Height, 1e-6);
            if (!Collides(box, owner.Where(o => !o.Row.Handle.Equals(pair.TargetHandle, StringComparison.OrdinalIgnoreCase))
                    .Select(o => o.Box!.Value).ToArray()))
                continue;
            Rect2? region = allowed.TryGetValue(pair.RecordId, out Rect2 value) ? value : null;
            Rect2? source = byHandle.TryGetValue(pair.SourceHandle, out ManifestRecord? origin) ? Box(origin) : null;
            // Keep the label a neighbour of its own source: jumping away would trade
            // a collision for a distant-label finding.
            double maxGap = source is Rect2 anchor ? anchor.Height * 1.9 : double.PositiveInfinity;
            // The defined wrap width of our own addition: zero means one unwrapped
            // line, which is the case that retracts as the glyph shrinks.
            double wrapWidth = Resolve(inspect, pair.TargetHandle) is { } targetId &&
                access.Read<Entity>(targetId, "correct-target-width", recordId: pair.RecordId) is MText own
                ? own.Width
                : 0;
            if (!TryPlan(box, height, wrapWidth, owner
                        .Where(o => !o.Row.Handle.Equals(pair.TargetHandle, StringComparison.OrdinalIgnoreCase))
                        .Select(o => o.Box!.Value).ToArray(), region, source, maxGap,
                    out Rect2 planned, out double factor, out double dx, out double dy, out string method))
                continue;
            corrections.Add(new(pair.RecordId, pair.TargetHandle, box, planned, factor, dx, dy, method));
        }
        tx.Commit();
        return corrections.ToArray();
    }

    private static bool TryPlan(Rect2 box, double height, double wrapWidth, Rect2[] occupied, Rect2? region,
        Rect2? source, double maxGap, out Rect2 planned, out double factor, out double dx, out double dy,
        out string method)
    {
        planned = box; factor = 1; dx = 0; dy = 0; method = string.Empty;
        double tolerance = Math.Max(1e-6, height * .03);
        // Prefer the smallest change that clears the neighbour: a short slide first,
        // then a smaller glyph (a single unwrapped line retracts as it shrinks), then
        // a narrower wrap that pulls the line end back before the obstacle column.
        foreach ((double scale, double wrap) in Attempts())
        {
            // Predicted sizes: an unwrapped MText keeps its top-left corner and
            // scales both sides; a wrapped one keeps its defined width.
            double width = wrapWidth > 0 || wrap < 1
                ? Math.Max(box.Width * (wrap < 1 ? wrap : 1), height * scale * 3)
                : box.Width * scale;
            double scaled = box.Height * scale;
            foreach ((double ox, double oy) in Offsets(height))
            {
                var candidate = new Rect2(box.Left + ox, box.Top + oy - scaled,
                    box.Left + ox + width, box.Top + oy);
                if (region is Rect2 target && !target.Contains(candidate, tolerance)) continue;
                if (source is Rect2 anchor && Gap(anchor, candidate) > maxGap) continue;
                if (Collides(candidate, occupied)) continue;
                planned = candidate;
                factor = scale;
                dx = ox;
                dy = oy;
                method = wrap < 1 ? "wrap" : ox == 0 && oy == 0 ? "shrink" : "slide";
                if (scale < 1) method += "-and-shrink";
                return true;
            }
        }
        return false;
    }

    // Working-database pass: move and resize our own additions only, and keep a
    // correction only when the realised box matches the plan.
    internal static Correction[] ApplyMoves(Database db, Correction[] plan, List<CadObjectAccess.Issue> issues)
    {
        if (plan.Length == 0) return [];
        var applied = new List<Correction>();
        using var tx = db.TransactionManager.StartTransaction();
        foreach (Correction correction in plan)
        {
            // These are our own additions and the pass rewrites them: open for write.
            // Opening for read and assigning properties aborts the console with an
            // internal eNotOpenForWrite error instead of a managed exception.
            if (OpenForWrite(db, tx, correction.TargetHandle) is not { } text) continue;
            double height = Math.Max(text.TextHeight, 1e-6);
            double width = text.Width;
            Point3d location = text.Location;
            text.TextHeight = height * correction.Factor;
            if (correction.Method.StartsWith("wrap", StringComparison.Ordinal))
                text.Width = Math.Max(text.Width > 0 ? text.Width * .7 : correction.Planned.Width, text.TextHeight * 3);
            text.Location = new Point3d(location.X + correction.OffsetX, location.Y + correction.OffsetY, location.Z);
            Rect2? realised = Measured(text);
            if (realised is not Rect2 box || !Fits(box, correction.Planned, Math.Max(height, 1e-6) * .05))
            {
                // The prediction did not hold; put the original back rather than
                // leave a half-applied move.
                text.TextHeight = height;
                text.Width = width;
                text.Location = location;
                continue;
            }
            applied.Add(correction with { Previous = box, Planned = box });
        }
        tx.Commit();
        return applied.ToArray();
    }

    private static bool Fits(Rect2 realised, Rect2 planned, double tolerance) =>
        Math.Abs(realised.Left - planned.Left) <= tolerance && Math.Abs(realised.Top - planned.Top) <= tolerance &&
        realised.Width <= planned.Width + tolerance && realised.Height <= planned.Height + tolerance;

    private static ManifestRecord[] Measure(IReadOnlyList<ManifestRecord> rows, Database db, CadObjectAccess access,
        IReadOnlyList<BilingualDrawingImporter.Pair> pairs, IReadOnlyDictionary<string, Rect2> estimates)
    {
        var targets = pairs.Where(p => p.Decision == "added")
            .Select(p => p.TargetHandle).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Only a block that received additions needs its neighbours measured, and only
        // text entities are read: dimensions keep their widened plan-time estimate.
        var owners = rows.Where(r => targets.Contains(r.Handle)).Select(r => r.OwnerPath).ToHashSet();
        var result = new List<ManifestRecord>(rows.Count);
        foreach (ManifestRecord row in rows)
        {
            if (!owners.Contains(row.OwnerPath))
            {
                result.Add(row);
                continue;
            }
            if (!long.TryParse(row.Handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value))
            {
                result.Add(Fallback(row, estimates));
                continue;
            }
            ObjectId id;
            try { id = db.GetObjectId(false, new Handle(value), 0); }
            catch (Autodesk.AutoCAD.Runtime.Exception) { result.Add(Fallback(row, estimates)); continue; }
            // Measure by handle, exactly like the saved-layout review: any text or
            // dimension the exporter already reported. Never enumerate block tables
            // here, and never open what the exporter did not report.
            Entity? entity = access.Read<Entity>(id, "correct-measure", recordId: row.RecordId);
            if (entity is not (DBText or MText or Dimension) || Measured(entity) is not { } box)
            {
                result.Add(Fallback(row, estimates));
                continue;
            }
            result.Add(Extents(row, box));
        }
        return result.ToArray();
    }

    // A dimension that cannot be read back keeps its widened plan-time estimate so
    // the pass still steers around it instead of pretending the space is free.
    private static ManifestRecord Fallback(ManifestRecord row, IReadOnlyDictionary<string, Rect2> estimates) =>
        estimates.TryGetValue(row.Handle, out Rect2 estimate) ? Extents(row, estimate) : row;

    private static ManifestRecord Extents(ManifestRecord row, Rect2 box) => row with {
        Geometry = row.Geometry with { Extents = new(new(box.Left, box.Bottom, 0), new(box.Right, box.Top, 0)) } };

    private static Rect2 Inflate(Rect2 box, double factor)
    {
        double dx = box.Width * (factor - 1) / 2, dy = box.Height * (factor - 1) / 2;
        return new Rect2(box.Left - dx, box.Bottom - dy, box.Right + dx, box.Top + dy);
    }

    private static Rect2? Box(ManifestRecord? row)
    {
        if (row?.Geometry.Extents is not { } extents) return null;
        var box = new Rect2(extents.Minimum.X, extents.Minimum.Y, extents.Maximum.X, extents.Maximum.Y);
        return double.IsFinite(box.Area) && box.Width > 1e-9 && box.Height > 1e-9 ? box : null;
    }

    private static IEnumerable<(double Scale, double Wrap)> Attempts()
    {
        yield return (1, 1);
        yield return (.9, 1);
        yield return (.8, 1);
        yield return (.7, 1);
        yield return (1, .75);
        yield return (.9, .75);
        yield return (.8, .75);
        yield return (.8, .5);
    }

    private static IEnumerable<(double X, double Y)> Offsets(double height)
    {
        yield return (0, 0);
        foreach (double step in new[] { 1.0, 1.6, 2.4 })
        {
            yield return (0, step * height);
            yield return (0, -step * height);
            yield return (-step * height, 0);
            yield return (step * height, 0);
        }
        foreach (double step in new[] { 1.0, 1.8 })
        {
            yield return (step * height, step * height);
            yield return (-step * height, step * height);
            yield return (step * height, -step * height);
            yield return (-step * height, -step * height);
        }
    }

    private static Rect2? Measured(Entity entity)
    {
        Bounds2d? bounds = entity is MText text && CadLayoutGeometry.TryFreshBounds(text) is { } fresh
            ? CadLayoutGeometry.TryBounds(entity) is { } native
                ? new Bounds2d(Math.Min(native.MinX, fresh.MinX), Math.Min(native.MinY, fresh.MinY),
                    Math.Max(native.MaxX, fresh.MaxX), Math.Max(native.MaxY, fresh.MaxY))
                : fresh
            : CadLayoutGeometry.TryBounds(entity);
        if (bounds is not { } b) return null;
        var box = new Rect2(b.MinX, b.MinY, b.MaxX, b.MaxY);
        return double.IsFinite(box.Area) && box.Width > 1e-9 && box.Height > 1e-9 ? box : null;
    }

    private static bool Collides(Rect2 candidate, IReadOnlyList<Rect2> occupied)
    {
        foreach (Rect2 other in occupied)
        {
            double width = Math.Min(candidate.Right, other.Right) - Math.Max(candidate.Left, other.Left);
            double height = Math.Min(candidate.Top, other.Top) - Math.Max(candidate.Bottom, other.Bottom);
            if (width <= 0 || height <= 0) continue;
            if (width * height / Math.Max(1e-9, Math.Min(candidate.Area, other.Area)) >= MinimumOverlapRatio) return true;
        }
        return false;
    }

    private static double Gap(Rect2 source, Rect2 target) => Math.Sqrt(
        Math.Pow(Math.Max(0, Math.Max(source.Left - target.Right, target.Left - source.Right)), 2) +
        Math.Pow(Math.Max(0, Math.Max(source.Bottom - target.Top, target.Bottom - source.Top)), 2));

    private static MText? OpenForWrite(Database db, Transaction tx, string handle)
    {
        ObjectId? id = Resolve(db, handle);
        if (id is not { } value) return null;
        try
        {
            return tx.GetObject(value, OpenMode.ForWrite) as MText;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return null; }
    }

    private static ObjectId? Resolve(Database db, string handle)
    {
        if (!long.TryParse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value)) return null;
        try
        {
            ObjectId id = db.GetObjectId(false, new Handle(value), 0);
            return id.IsValid && !id.IsErased ? id : null;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return null; }
    }
}

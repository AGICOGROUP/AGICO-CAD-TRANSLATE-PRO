using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

internal sealed class BilingualPlacementTrace
{
    internal sealed record Rejection(string Rule, Rect2 Candidate, Rect2? Obstacle, string? Handle,
        Segment2? Boundary, double TextHeight, double TextWidth, double Rotation);
    internal Dictionary<string, int> Counts { get; } = new();
    internal List<Rejection> Examples { get; } = new();
    internal Rect2 Allowed { get; set; }
    internal string? RegionId { get; set; }

    internal bool Reject(string rule, Rect2 candidate, MText text, Rect2? obstacle = null,
        string? handle = null, Segment2? boundary = null)
    {
        Counts[rule] = Counts.GetValueOrDefault(rule) + 1;
        // Aggregate all attempts, retain at most three examples per reason.
        if (Counts[rule] <= 3 && Examples.Count < 18)
            Examples.Add(new(rule, candidate, obstacle, handle, boundary, text.TextHeight, text.Width, text.Rotation));
        return false;
    }

    internal object Report(CadLayoutBaseline baseline, IReadOnlyList<BilingualDrawingImporter.Pair> pairs, string definition)
    {
        // Resolve text handles only for failed labels, not in the candidate search loop.
        string? TextHandle(Rect2 obstacle)
        {
            foreach (var d in baseline.Definitions)
            foreach (var t in d.Texts.Select(t => (Handle: t.EntityHandle, Bounds: t.Source.Bounds))
                .Concat(pairs.Where(p => p.DefinitionName == d.Name).Select(p => (Handle: p.TargetHandle, Bounds: p.Bounds))))
            {
                if (d.Name == definition && t.Bounds == obstacle) return t.Handle;
                if (d.Name != definition && InstanceOccupancyProjection.Project(t.Bounds, d.Name, definition, baseline.BlockInstances).Contains(obstacle))
                    return t.Handle;
            }
            return null; // A reserved table slot has bounds, but is not itself an entity.
        }
        return new { allowed = Allowed, regionId = RegionId, counts = Counts,
            examples = Examples.Select(e => e.Rule == "text-overlap" && e.Obstacle is Rect2 b
                ? e with { Handle = TextHandle(b) } : e).ToArray(),
            note = "First rejecting rule per candidate; bounded samples, not all collisions. Null handle may denote a reserved slot; boundary endpoints identify topology segments." };
    }
}

internal static class BilingualPlacementChecks
{
    internal static Rect2 Footprint(MText text)
    {
        double w = Math.Max(text.TextHeight, text.ActualWidth) * 1.02;
        double h = Math.Max(text.TextHeight, text.ActualHeight) * 1.02;
        var estimated = TextBoundsEstimator.FromActualBox(new Point2(0, 0), w, h, TextAttachmentKind.TopLeft, text.Rotation);
        // These are new target MTexts, not old source-column boxes. SHX/native
        // extents can exceed ActualWidth/Height even with the existing 2% padding.
        if (CadLayoutGeometry.TryBounds(text) is not { } native) return estimated;
        return new Rect2(Math.Min(estimated.Left, native.MinX-text.Location.X),
            Math.Min(estimated.Bottom, native.MinY-text.Location.Y),
            Math.Max(estimated.Right, native.MaxX-text.Location.X),
            Math.Max(estimated.Top, native.MaxY-text.Location.Y));
    }

    internal static void Move(MText text, Rect2 footprint, double left, double top, double z) =>
        text.Location = new Point3d(left - footprint.Left, top - footprint.Top, z);

    internal static double AlongText(Rect2 box, double rotation) =>
        Math.Abs(Math.Cos(rotation)) * box.Width + Math.Abs(Math.Sin(rotation)) * box.Height;

    internal static bool Accept(MText text, Rect2 proposed, Rect2 allowed, CadLayoutText source,
        CadDefinitionTopology definition, IReadOnlyList<Rect2> occupied, double padding,
        BilingualPlacementTrace? trace, out Rect2 actual)
    {
        actual = proposed;
        if (!Check(proposed)) return false;
        var measured = CadLayoutGeometry.TryFreshBounds(text);
        if (measured is null) return Reject("missing-native-bounds", proposed);
        actual = new Rect2(measured.MinX, measured.MinY, measured.MaxX, measured.MaxY);
        if (CadLayoutGeometry.TryBounds(text) is { } native)
            actual = new Rect2(Math.Min(actual.Left,native.MinX),Math.Min(actual.Bottom,native.MinY),
                Math.Max(actual.Right,native.MaxX),Math.Max(actual.Top,native.MaxY));
        return Check(actual);

        bool Reject(string rule, Rect2 b, Rect2? obstacle = null, string? handle = null, Segment2? boundary = null) =>
            trace?.Reject(rule, b, text, obstacle, handle, boundary) ?? false;
        bool Check(Rect2 b)
        {
            if (!double.IsFinite(b.Area) || b.Width <= 0 || b.Height <= 0) return Reject("invalid-bounds", b);
            if (!allowed.Contains(b, 1e-6)) return Reject("outside-region", b, allowed);
            foreach (Rect2 o in occupied)
                if (BilingualDrawingImporter.Intersects(b, o, padding)) return Reject("text-overlap", b, o);
            // Bilingual additions may cross drawing lines. Only text occupancy
            // is a hard collision; source geometry remains unmodified.
            return true;
        }
    }
}

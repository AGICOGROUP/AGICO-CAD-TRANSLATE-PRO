using Autodesk.AutoCAD.DatabaseServices;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

internal static class BilingualSignaturePlacement
{
    // Signature cells express field ownership. Unlike ordinary diagram lines,
    // crossing their separators makes the translated field ambiguous.
    internal static bool TryPlace(MText text, string contents, CadLayoutText source,
        CadDefinitionTopology definition, Rect2 cell, IReadOnlyList<Rect2> occupied, double z,
        out Rect2 result, out double usedScale, BilingualPlacementTrace? trace)
    {
        result = source.Source.Bounds; usedScale = 0;
        double height = source.Source.OriginalTextHeight, rotation = text.Rotation;
        double gap = height * .25;
        var allowed = new Rect2(cell.Left + gap, cell.Bottom + gap, cell.Right - gap, cell.Top - gap);
        if (allowed.Width <= 0 || allowed.Height <= 0) return false;
        if (trace is not null) trace.Allowed = allowed;
        foreach (double scale in new[] { 1.0, .9, .8, .7 })
        foreach (bool wrap in new[] { false, true })
        foreach (double direction in new[] { rotation, 0d }.Distinct())
        {
            text.Rotation = direction;
            text.TextHeight = height * scale;
            text.Contents = contents;
            text.Width = wrap ? Math.Max(height, BilingualPlacementChecks.AlongText(allowed, direction)) : 0;
            var footprint = BilingualPlacementChecks.Footprint(text);
            double w = footprint.Width, h = footprint.Height;
            if (w > allowed.Width || h > allowed.Height) continue;
            var box = source.Source.Bounds;
            var preferred = new[] {
                new Rect2(box.Right + gap, cell.Center.Y - h / 2, box.Right + gap + w, cell.Center.Y + h / 2),
                new Rect2(box.Left - gap - w, cell.Center.Y - h / 2, box.Left - gap, cell.Center.Y + h / 2)
            };
            foreach (var slot in preferred.Concat(BilingualLocalPlacement.Candidates(allowed, box, w, h, occupied, height * .12)).Distinct().Take(34))
            {
                BilingualPlacementChecks.Move(text, footprint, slot.Left, slot.Top, z);
                if (!BilingualPlacementChecks.Accept(text, slot, allowed, source, definition, occupied,
                    height * .12, trace, out var actual)) continue;
                result = actual; usedScale = scale; return true;
            }
        }
        text.Rotation = rotation;
        return false;
    }
}

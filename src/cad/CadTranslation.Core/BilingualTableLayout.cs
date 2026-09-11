namespace CadTranslation.Core;

/// <summary>Connected grid cells only; isolated frames and free labels are not tables.</summary>
public static class BilingualTableLayout
{
    // Topology may omit cells whose text crosses a line. Recover only real closed
    // strips from repeated horizontal spans and continuous vertical side edges.
    public static IReadOnlyList<Rect2[]> CompleteGroups(IEnumerable<LayoutRegion> regions, IReadOnlyList<Segment2> segments)
    {
        var all=regions.ToList();
        const double tolerance=.001;
        var vertical=segments.Where(s=>s.IsVertical(tolerance)).ToArray();
        foreach(var span in segments.Where(s=>s.IsHorizontal(tolerance) && s.MaxX-s.MinX>tolerance)
            .GroupBy(s=>(Math.Round(s.MinX,3),Math.Round(s.MaxX,3))))
        {
            var lines=span.OrderBy(s=>s.Start.Y).ToArray();
            var ys=lines.Select(s=>s.Start.Y).Distinct().Order().ToArray();
            if(ys.Length<4) continue;
            double left=lines[0].MinX,right=lines[0].MaxX;
            var gaps=ys.Zip(ys.Skip(1),(a,b)=>b-a).Where(h=>h>tolerance).Order().ToArray();
            double typical=gaps[gaps.Length/2];
            for(int i=1;i<ys.Length;i++)
            {
                if(ys[i]-ys[i-1]>typical*1.6) continue;
                var edges=vertical.Where(s=>s.Start.X>=left-tolerance && s.Start.X<=right+tolerance && s.MinY<=ys[i-1]+tolerance && s.MaxY>=ys[i]-tolerance)
                    .Select(s=>s.Start.X).Distinct().Order().ToArray();
                for(int x=1;x<edges.Length;x++)
                    if(edges[x]-edges[x-1]>tolerance) all.Add(new LayoutRegion("bilingual-grid-strip",LayoutRegionKind.TableCell,new(edges[x-1],ys[i-1],edges[x],ys[i])));
            }
        }
        // A recovered full row may contain existing subdivided cells. They belong
        // to the same table even when their outer edges do not touch.
        return Groups(all);
    }
    public static IReadOnlyList<Rect2[]> Groups(IEnumerable<LayoutRegion> regions)
    {
        var remaining = regions.Where(r => r.Kind == LayoutRegionKind.TableCell).Select(r => r.Bounds).Distinct().ToList();
        var result = new List<Rect2[]>();
        while (remaining.Count > 0)
        {
            var group = new List<Rect2> { remaining[0] };
            remaining.RemoveAt(0);
            for (int i = 0; i < group.Count; i++)
                for (int j = remaining.Count - 1; j >= 0; j--)
                    if (Adjacent(group[i], remaining[j])) { group.Add(remaining[j]); remaining.RemoveAt(j); }
            if (group.Count >= 2) result.Add(group.ToArray());
        }
        return result;
    }

    private static bool Adjacent(Rect2 a, Rect2 b) => a.Contains(b) || b.Contains(a) ||
        ((Math.Abs(a.Right - b.Left) < 1e-5 || Math.Abs(b.Right - a.Left) < 1e-5) && Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom) > 1e-5) ||
        ((Math.Abs(a.Top - b.Bottom) < 1e-5 || Math.Abs(b.Top - a.Bottom) < 1e-5) && Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left) > 1e-5);

    public static Rect2[] SideSlots(Rect2 table, IReadOnlyList<Rect2> rows, double width, double gap, bool left) =>
        rows.Select(r => new Rect2(left ? table.Left - gap - width : table.Right + gap,
            r.Bottom + gap, left ? table.Left - gap : table.Right + gap + width, r.Top - gap)).ToArray();
}

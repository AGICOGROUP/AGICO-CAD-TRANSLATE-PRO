namespace CadTranslation.Core;

/// <summary>Connected grid cells only; isolated frames and free labels are not tables.</summary>
public static class BilingualTableLayout
{
    // A header is part of its table. Only positive title-field evidence can split
    // an attached title panel from a schedule; row-height changes are not evidence.
    public static IReadOnlyList<Rect2[]> TranslationGroups(IEnumerable<LayoutRegion> regions, IReadOnlyList<Segment2> segments,
        IReadOnlyList<(Rect2 Bounds, string Text)>? labels = null)
    {
        var result = new List<Rect2[]>();
        foreach (var connected in CompleteGroups(regions, segments))
        {
            var cells = connected.Where(c => !connected.Any(b => b != c && c.Contains(b) && b.Area < c.Area - 1e-5)).ToArray();
            bool split = false;
            if (labels is not null)
            {
                var cuts = cells.Select(c => c.Top).Distinct().Order()
                    .Where(y => !cells.Any(c => c.Bottom < y - 1e-5 && c.Top > y + 1e-5));
                foreach (double cut in cuts)
                {
                    var lower = cells.Where(c => c.Top <= cut + 1e-5).ToArray();
                    var upper = cells.Where(c => c.Bottom >= cut - 1e-5).ToArray();
                    if (lower.Length < 2 || upper.Select(c=>c.Bottom).Distinct().Count() < 3) continue;
                    IEnumerable<string> In(Rect2[] group) => labels.Where(t=>group.Any(c=>c.Contains(
                        new Rect2(t.Bounds.Center.X,t.Bounds.Center.Y,t.Bounds.Center.X,t.Bounds.Center.Y)))).Select(t=>t.Text);
                    if (!BilingualTitlePanel.IsTitlePanel(In(lower)) || BilingualTitlePanel.IsTitlePanel(In(upper))) continue;
                    result.Add(lower); result.Add(upper); split = true; break;
                }
            }
            if (!split) result.Add(cells);
        }
        return result;
    }

    // Topology may omit cells whose text crosses a line. Recover only real closed
    // strips from repeated horizontal spans and continuous vertical side edges.
    public static IReadOnlyList<Rect2[]> CompleteGroups(IEnumerable<LayoutRegion> regions, IReadOnlyList<Segment2> segments)
    {
        var all=regions.ToList();
        const double tolerance=.001;
        var horizontal=Merge(segments.Where(s=>s.IsHorizontal(tolerance)),true,tolerance);
        var vertical=Merge(segments.Where(s=>s.IsVertical(tolerance)),false,tolerance);
        var ys=horizontal.Select(s=>s.Start.Y).Distinct().Order().ToArray();
        // Sweep local bands. Unrelated ticks elsewhere in the drawing cannot
        // subdivide a merged cell, and segmented collinear edges stay continuous.
        for(int i=1;i<ys.Length;i++)
        {
            double y=(ys[i]+ys[i-1])/2;
            var sides=vertical.Where(s=>s.MinY<y && s.MaxY>y).OrderBy(s=>s.Start.X).ToArray();
            for(int x=1;x<sides.Length;x++)
            {
                var a=sides[x-1];var b=sides[x];double left=a.Start.X,right=b.Start.X;
                if(right-left<=tolerance) continue;
                var cross=horizontal.Where(s=>s.MinX<=left+tolerance && s.MaxX>=right-tolerance).ToArray();
                var bottom=cross.Where(s=>s.Start.Y<y).OrderByDescending(s=>s.Start.Y).FirstOrDefault();
                var top=cross.Where(s=>s.Start.Y>y).OrderBy(s=>s.Start.Y).FirstOrDefault();
                if(top==default || bottom==default || top.Start.Y-bottom.Start.Y<=tolerance ||
                    bottom.MinX>left+tolerance || bottom.MaxX<right-tolerance || top.MinX>left+tolerance || top.MaxX<right-tolerance ||
                    a.MinY>bottom.Start.Y+tolerance || b.MinY>bottom.Start.Y+tolerance || a.MaxY<top.Start.Y-tolerance || b.MaxY<top.Start.Y-tolerance) continue;
                all.Add(new LayoutRegion("bilingual-grid-cell",LayoutRegionKind.TableCell,new(left,bottom.Start.Y,right,top.Start.Y)));
            }
        }
        // A recovered full row may contain existing subdivided cells. They belong
        // to the same table even when their outer edges do not touch.
        // Remove enclosing frame-sized pseudo-cells before connectivity; doing
        // this after grouping lets a sheet frame join unrelated tables/legends.
        var cells=all.Where(r=>r.Kind==LayoutRegionKind.TableCell).DistinctBy(r=>r.Bounds).ToArray();
        return Groups(cells.Where(a=>!cells.Any(b=>a.Bounds!=b.Bounds && a.Bounds.Contains(b.Bounds) &&
            b.Bounds.Area<a.Bounds.Area-1e-5)));
    }

    private static Segment2[] Merge(IEnumerable<Segment2> lines,bool horizontal,double tolerance)
    {
        var merged=new List<Segment2>();
        foreach(var group in lines.GroupBy(s=>Math.Round(horizontal?s.Start.Y:s.Start.X,3)))
        {
            double axis=horizontal?group.First().Start.Y:group.First().Start.X;
            double start=double.NaN,end=double.NaN;
            void Add(){if(double.IsFinite(start))merged.Add(horizontal?new(new(start,axis),new(end,axis)):new(new(axis,start),new(axis,end)));}
            foreach(var line in group.OrderBy(s=>horizontal?s.MinX:s.MinY))
            {
                double lo=horizontal?line.MinX:line.MinY,hi=horizontal?line.MaxX:line.MaxY;
                if(!double.IsFinite(start)){start=lo;end=hi;}
                else if(lo<=end+tolerance)end=Math.Max(end,hi);
                else {Add();start=lo;end=hi;}
            }
            Add();
        }
        return merged.ToArray();
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

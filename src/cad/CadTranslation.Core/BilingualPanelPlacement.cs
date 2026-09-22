namespace CadTranslation.Core;

public static class BilingualPanelPlacement
{
    public static IEnumerable<Rect2> SideDestinations(Rect2 source, double height, double gap,
        IReadOnlyList<Rect2> frames, IReadOnlyList<Rect2> occupied)
    {
        double width=source.Width, bottom=source.Top-height;
        Rect2 Right(double edge) => new(edge+gap,bottom,edge+gap+width,source.Top);
        Rect2 Left(double edge) => new(edge-gap-width,bottom,edge-gap,source.Top);
        var result=new List<Rect2>{Right(source.Right),Left(source.Left)};
        foreach(var frame in frames.Where(f=>f.Contains(source)))
        { result.Add(Right(frame.Right)); result.Add(Left(frame.Left)); }
        var neighbors=occupied.Where(b=>b.Bottom<source.Top+gap && b.Top>bottom-gap).ToArray();
        foreach(var b in neighbors.OrderBy(b=>BilingualLocalPlacement.Gap(source,b)).Take(32))
        {
            if(b.Right>=source.Right)result.Add(Right(b.Right));
            if(b.Left<=source.Left)result.Add(Left(b.Left));
        }
        if(neighbors.Length>0)
        { result.Add(Right(Math.Max(source.Right,neighbors.Max(b=>b.Right)))); result.Add(Left(Math.Min(source.Left,neighbors.Min(b=>b.Left)))); }
        return result.Distinct().Where(b=>AvoidsOtherFrames(source,b,frames))
            .OrderBy(b=>CrossesContainingFrame(source,b,frames))
            .ThenBy(b=>BilingualLocalPlacement.Gap(source,b));
    }

    // Vertical neighbours keep the block aligned with its source column. The
    // bands above and below a containing frame are offered so a long translation
    // can sit next to its source instead of far off to one side.
    public static IEnumerable<Rect2> VerticalDestinations(Rect2 source, double height, double gap,
        IReadOnlyList<Rect2> frames, IReadOnlyList<Rect2> occupied)
    {
        double width = source.Width;
        Rect2 Above(double edge) => new(source.Left, edge + gap, source.Left + width, edge + gap + height);
        Rect2 Below(double edge) => new(source.Left, edge - gap - height, source.Left + width, edge - gap);
        var result = new List<Rect2> { Above(source.Top), Below(source.Bottom) };
        foreach (var frame in frames.Where(f => f.Contains(source)))
        { result.Add(Above(frame.Top)); result.Add(Below(frame.Bottom)); }
        return result.Distinct().Where(b => AvoidsOtherFrames(source, b, frames)
                && !occupied.Any(o => BilingualTablePlacement.Overlap(b, o)))
            .OrderBy(b => CrossesContainingFrame(source, b, frames))
            .ThenBy(b => BilingualLocalPlacement.Gap(source, b));
    }

    public static bool CrossesContainingFrame(Rect2 source,Rect2 target,IEnumerable<Rect2> frames) =>
        frames.Any(f=>f.Contains(source) && !f.Contains(target) && BilingualTablePlacement.Overlap(f,target));

    public static bool AvoidsOtherFrames(Rect2 source,Rect2 target,IEnumerable<Rect2> frames) =>
        !frames.Any(f=>!f.Contains(source) && BilingualTablePlacement.Overlap(f,target));
    public static IEnumerable<Rect2> OutsideFrames(Rect2 source,double w,double h,double gap,IEnumerable<Rect2> frames)
    {
        // Align to the source, not the sheet center, so a supplement keeps its
        // same-side association. Unrelated/neighboring sheets are never anchors.
        return frames.Where(f=>f.Contains(source) && f.Area>source.Area).Distinct().SelectMany(f=>new Rect2[]{
            new(f.Right+gap,source.Top-h,f.Right+gap+w,source.Top),
            new(f.Left-gap-w,source.Top-h,f.Left-gap,source.Top),
            new(source.Left,f.Top+gap,source.Left+w,f.Top+gap+h),
            new(source.Left,f.Bottom-gap-h,source.Left+w,f.Bottom-gap)
        }).OrderBy(b=>BilingualLocalPlacement.Gap(source,b));
    }

    public static IEnumerable<Rect2> Destinations(Rect2 source,double w,double h,double height,Rect2 allowed,Rect2? frame,
        IReadOnlyList<Rect2>? containingFrames = null)
    {
        var result=new List<Rect2>();
        foreach(double gap in new[]{height,3*height,6*height,10*height})
        {
            result.AddRange([
                new(source.Left,source.Bottom-gap-h,source.Left+w,source.Bottom-gap),
                new(source.Left,source.Top+gap,source.Left+w,source.Top+gap+h),
                new(source.Right+gap,source.Top-h,source.Right+gap+w,source.Top),
                new(source.Left-gap-w,source.Top-h,source.Left-gap,source.Top),
                new(source.Center.X-w/2,source.Bottom-gap-h,source.Center.X+w/2,source.Bottom-gap),
                new(allowed.Left+gap,source.Bottom-gap-h,allowed.Left+gap+w,source.Bottom-gap),
                new(allowed.Right-gap-w,source.Bottom-gap-h,allowed.Right-gap,source.Bottom-gap),
                new(source.Left,allowed.Bottom+gap,source.Left+w,allowed.Bottom+gap+h)]);
            if(frame is Rect2 inner)
                result.AddRange([
                    new(source.Left,inner.Top+gap,source.Left+w,inner.Top+gap+h),
                    new(source.Left,inner.Bottom-gap-h,source.Left+w,inner.Bottom-gap),
                    new(inner.Right-gap-w,inner.Top-gap-h,inner.Right-gap,inner.Top-gap),
                    new(inner.Left+gap,inner.Bottom+gap,inner.Left+gap+w,inner.Bottom+gap+h)]);
        }
        result.AddRange(OutsideFrames(source,w,h,height,containingFrames ?? (frame is Rect2 f ? [f] : [])));
        return result.Distinct().Where(b=>allowed.Contains(b) && AvoidsOtherFrames(source,b,containingFrames ?? []))
            .OrderBy(b=>frame is Rect2 f && !f.Contains(b))
            .ThenBy(b=>BilingualLocalPlacement.Gap(source,b));
    }
}

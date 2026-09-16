namespace CadTranslation.Core;

public static class BilingualPanelPlacement
{
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

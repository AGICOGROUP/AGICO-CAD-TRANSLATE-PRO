using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

// Reserve complete reading groups before independent labels consume the whitespace.
internal static class BilingualGroupLayout
{
    internal readonly record struct Slot(Rect2 Bounds, double Height, double WrapWidth, string Strategy, string DisplayText);
    internal static string Contents(string target, string language) =>
        (language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? @"\FSimSun;" : @"\FArial;") +
        target.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}").Replace("\r", "").Replace("\n", "\\P");

    internal static Dictionary<string, Slot> Plan(Database db, CadLayoutBaseline baseline,
        LayoutWriteInput[] inputs, Dictionary<string,List<Rect2>> occupied, string language,
        CadObjectAccess access, List<object> decisions)
    {
        var result = new Dictionary<string,Slot>();
        var byId = inputs.ToDictionary(i => i.Manifest.RecordId);
        var sourcePlain=inputs.ToDictionary(i=>i.Manifest.RecordId,i=>BilingualDrawingImporter.Plain(i.Manifest.RawText));
        var targetPlain=inputs.ToDictionary(i=>i.Manifest.RecordId,i=>BilingualDrawingImporter.Plain(i.RestoredText));
        var changed = inputs.Where(i => targetPlain[i.Manifest.RecordId] != sourcePlain[i.Manifest.RecordId])
            .ToDictionary(i => i.Manifest.RecordId);
        var eligible = new HashSet<string>();
        foreach(var d in baseline.Definitions)
        foreach(var t in d.Texts.Where(t=>changed.ContainsKey(t.RecordId)))
        {
            var row=changed[t.RecordId].Manifest;
            if(Math.Abs(row.Geometry.RotationRadians)>1e-6 || row.ObjectType is not ("AcDbText" or "AcDbMText") ||
                BilingualFixedLabelPolicy.ContainsEmbeddedEnglish(row.RawText)) continue;
            if(d.Texts.Any(other=>other.RecordId!=t.RecordId && sourcePlain.ContainsKey(other.RecordId) &&
                Math.Abs(other.Source.Bounds.Center.Y-t.Source.Bounds.Center.Y)<t.Source.OriginalTextHeight*3 &&
                Math.Abs(other.Source.Bounds.Center.X-t.Source.Bounds.Center.X)<t.Source.OriginalTextHeight*12 &&
                BilingualLabelEquivalence.Matches(targetPlain[t.RecordId],sourcePlain[other.RecordId]))) continue;
            eligible.Add(t.RecordId);
        }
        foreach (var d in baseline.Definitions)
        {
            if(!d.Texts.Any(t=>eligible.Contains(t.RecordId))) continue;
            var tableIds = new HashSet<string>();
            var groups = new List<(CadLayoutText[] Rows, Rect2 Source, string Strategy)>();
            foreach (var cells in BilingualTableLayout.CompleteGroups(d.Regions,d.BoundarySegments))
            {
                // A text's box may cross a grid line; its center still identifies its row.
                var members = d.Texts.Where(t => cells.Any(c => c.Contains(Point(t.Source.Bounds.Center)))).ToArray();
                foreach (var t in members) tableIds.Add(t.RecordId);
                var tableBox=Union(cells);
                double bodyHeight=members.Select(t=>t.Source.OriginalTextHeight).DefaultIfEmpty(1).Max();
                var headings=d.Texts.Where(t=>Eligible(t) && !members.Contains(t) &&
                    t.Source.Bounds.Center.X>=tableBox.Left && t.Source.Bounds.Center.X<=tableBox.Right &&
                    t.Source.Bounds.Bottom>=tableBox.Top-bodyHeight*.25 && t.Source.Bounds.Bottom<=tableBox.Top+bodyHeight*2 &&
                    System.Text.RegularExpressions.Regex.IsMatch(System.Text.RegularExpressions.Regex.Replace(sourcePlain[t.RecordId],@"\s+",""),"技术性能|技术参数|技术特征|设备表|材料表|Technical|Schedule",System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToArray();
                foreach(var heading in headings) tableIds.Add(heading.RecordId);
                var requests = members.Concat(headings).Where(t => Eligible(t)).OrderByDescending(t => t.Source.Bounds.Center.Y).ThenBy(t => t.Source.Bounds.Left).ToArray();
                var rowCount = cells.Select(c => Math.Round(c.Center.Y,3)).Distinct().Count();
                if (requests.Length < 3 || rowCount < 3) continue;
                // Signature/company panels are not reading tables.
                var labels = members.Where(t=>byId.ContainsKey(t.RecordId)).Select(t=>sourcePlain[t.RecordId]).ToArray();
                if (BilingualTitlePanel.IsTitlePanel(labels) || System.Text.RegularExpressions.Regex.IsMatch(
                    string.Join(" ", labels),"项目经理|批准|审核|校核|会签|Approved|Checked|Reviewed",System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                groups.Add((requests,tableBox,"table-aligned-block"));
            }

            var remaining = d.Texts.Where(t => byId.ContainsKey(t.RecordId) && !tableIds.Contains(t.RecordId) &&
                t.Source.OriginalTextHeight > 0 && Math.Abs(byId[t.RecordId].Manifest.Geometry.RotationRadians) < 1e-6 &&
                byId[t.RecordId].Manifest.ObjectType is "AcDbText" or "AcDbMText").ToList();
            double narrativeHeight = remaining.Select(t => t.Source.OriginalTextHeight)
                .Where(value => value > 0).OrderBy(value => value).DefaultIfEmpty(1).ElementAt(remaining.Count / 2);
            NarrativeOccupancyGroup[] narrativeGroups = AuthoritativeNarrativeSelector.SelectPanelGroups(
                remaining.Select(t => new FragmentedNarrativeSample(
                    t.RecordId,
                    t.Source.Bounds,
                    sourcePlain[t.RecordId],
                    Eligible(t))).ToArray(),
                narrativeHeight);
            var narrativeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (NarrativeOccupancyGroup narrative in narrativeGroups)
            {
                CadLayoutText[] requests = narrative.MemberIds
                    .Where(eligible.Contains)
                    .Select(id => d.Texts.First(text => text.RecordId == id))
                    .OrderByDescending(t => t.Source.Bounds.Center.Y)
                    .ThenBy(t => t.Source.Bounds.Left)
                    .ToArray();
                if (requests.Length < 3) continue;
                groups.Add((requests, narrative.Region.Bounds, "note-block"));
                foreach (string id in narrative.MemberIds) narrativeIds.Add(id);
            }
            remaining.RemoveAll(t => narrativeIds.Contains(t.RecordId));
            while (remaining.Count > 0)
            {
                var seed=remaining[0]; remaining.RemoveAt(0);
                var members=new List<CadLayoutText>{seed};
                for(int k=0;k<members.Count;k++)
                for(int j=remaining.Count-1;j>=0;j--)
                {
                    var a=members[k]; var b=remaining[j];
                    double h=Math.Max(a.Source.OriginalTextHeight,b.Source.OriginalTextHeight);
                    double gap=Math.Max(a.Source.Bounds.Bottom,b.Source.Bounds.Bottom)-Math.Min(a.Source.Bounds.Top,b.Source.Bounds.Top);
                    if (Math.Abs(a.Source.Bounds.Left-b.Source.Bounds.Left)>h*.8 || gap>h*1.8 ||
                        Math.Abs(a.Source.Bounds.Center.Y-b.Source.Bounds.Center.Y)<h*.5 ||
                        Math.Min(a.Source.OriginalTextHeight,b.Source.OriginalTextHeight)<h*.7 ||
                        byId[a.RecordId].Manifest.Properties.Layer!=byId[b.RecordId].Manifest.Properties.Layer) continue;
                    var connector=new Segment2(a.Source.Bounds.Center,b.Source.Bounds.Center);
                    if(d.BoundarySegments.Any(s=>SegmentsCross(connector,s))) continue;
                    members.Add(b); remaining.RemoveAt(j);
                }
                var requests=members.Where(Eligible).OrderByDescending(t=>t.Source.Bounds.Center.Y).ThenBy(t=>t.Source.Bounds.Left).ToArray();
                bool paragraph=requests.Length==1 && targetPlain[requests[0].RecordId].Length>=100;
                if(requests.Length>=3 || paragraph) groups.Add((requests,Union(members.Select(t=>t.Source.Bounds)),"note-block"));
            }

            var frameRegions=d.Regions.Where(r=>r.Kind==LayoutRegionKind.ClosedFrame).ToList();
            foreach(var other in baseline.Definitions.Where(other=>other.Name!=d.Name))
            foreach(var region in other.Regions.Where(r=>r.Kind==LayoutRegionKind.ClosedFrame))
                frameRegions.AddRange(InstanceOccupancyProjection.Project(region.Bounds,other.Name,d.Name,baseline.BlockInstances)
                    .Select(b=>new LayoutRegion("projected-group-frame",LayoutRegionKind.ClosedFrame,b)));
            foreach(var group in groups.OrderByDescending(g=>g.Source.Area))
            {
                double sourceHeight=group.Rows.Min(t=>t.Source.OriginalTextHeight);
                var frame=frameRegions.Where(r=>r.Bounds.Contains(group.Source) && r.Bounds.Area>group.Source.Area*1.1)
                    .OrderBy(r=>r.Bounds.Area).FirstOrDefault();
                var expanded=new Rect2(group.Source.Left-sourceHeight*60,group.Source.Bottom-sourceHeight*60,
                    group.Source.Right+sourceHeight*60,group.Source.Top+sourceHeight*60);
                var allowed=frame is null || group.Strategy=="table-aligned-block"
                    ? frame?.Bounds ?? expanded
                    : Union(new[]{frame.Bounds,expanded});
                var textObstacles=occupied[d.Name].Where(b=>BilingualDrawingImporter.Intersects(allowed,b,sourceHeight*.2)).ToArray();
                var display=group.Rows.Select(row=>{
                    if(group.Strategy!="table-aligned-block") return targetPlain[row.RecordId];
                    var numbers=d.Texts.Where(t=>sourcePlain.ContainsKey(t.RecordId) &&
                        System.Text.RegularExpressions.Regex.IsMatch(sourcePlain[t.RecordId],@"^\s*\d+[a-z]?\s*$") &&
                        t.Source.Bounds.Right<=row.Source.Bounds.Left && row.Source.Bounds.Left-t.Source.Bounds.Right<sourceHeight*4 &&
                        Math.Abs(t.Source.Bounds.Center.Y-row.Source.Bounds.Center.Y)<sourceHeight).OrderBy(t=>row.Source.Bounds.Left-t.Source.Bounds.Right).ToArray();
                    return (numbers.Length>0?sourcePlain[numbers[0].RecordId].Trim()+". ":"")+targetPlain[row.RecordId];
                }).ToArray();
                bool placed=false;
                var rejected=new List<object>();
                foreach(double scale in new[]{.85,.7})
                {
                    double height=sourceHeight*scale, gap=height*(group.Strategy=="table-aligned-block"?.30:.45);
                    foreach(double width in new[]{Math.Max(group.Source.Width,height*24),Math.Max(group.Source.Width*.75,height*18),Math.Max(group.Source.Width*1.4,height*30)}.Distinct())
                    {
                        var sizes=new List<Rect2>();
                        for(int index=0;index<group.Rows.Length;index++)
                        {
                            using var probe=Probe(db,display[index],height,width,language);
                            sizes.Add(BilingualPlacementChecks.Footprint(probe));
                        }
                        double w=sizes.Max(b=>b.Width), total=sizes.Sum(b=>b.Height+gap)-gap;
                        if(w>allowed.Width || total>allowed.Height) continue;
                        foreach(var destination in Destinations(group.Source,w,total,sourceHeight,allowed,frame?.Bounds))
                        {
                            var textConflict=textObstacles.Where(b=>BilingualDrawingImporter.Intersects(destination,b,height*.2)).ToArray();
                            var lines=d.BoundarySegments.Where(s=>BilingualDrawingImporter.Crosses(destination,s)).Take(1).ToArray();
                            var geometry=d.ProtectedGeometry.Where(g=>!g.Bounds.Contains(group.Source) && BilingualDrawingImporter.Intersects(destination,g.Bounds,0)).Take(1).ToArray();
                            if(!allowed.Contains(destination) || textConflict.Length>0 || lines.Length>0 || geometry.Length>0)
                            {
                                if(rejected.Count==20) rejected.RemoveAt(0);
                                rejected.Add(new{height,width,destination,outside=!allowed.Contains(destination),text=textConflict.Take(1).ToArray(),lines,geometry=geometry.Select(g=>new{g.ObjectType,g.Bounds}).ToArray()});
                                continue;
                            }
                            double top=destination.Top;
                            var planned=new List<(string Id,Slot Slot)>();
                            for(int i=0;i<group.Rows.Length;i++)
                            {
                                var row=group.Rows[i];
                                // A common left edge and a full measured row prevent independent drift.
                                var slot=new Rect2(destination.Left,top-sizes[i].Height,destination.Right,top);
                                planned.Add((row.RecordId,new(slot,height,width,group.Strategy,display[i])));
                                top-=sizes[i].Height+gap;
                            }
                            foreach(var p in planned) result[p.Id]=p.Slot;
                            occupied[d.Name].Add(destination);
                            foreach(var name in occupied.Keys.Where(n=>n!=d.Name))
                                occupied[name].AddRange(InstanceOccupancyProjection.Project(destination,d.Name,name,baseline.BlockInstances));
                            decisions.Add(new{strategy=group.Strategy,source=group.Source,destination,recordIds=group.Rows.Select(t=>t.RecordId).ToArray(),height});
                            placed=true; break;
                        }
                        if(placed) break;
                    }
                    if(placed) break;
                }
                if(!placed) decisions.Add(new{strategy=group.Strategy,source=group.Source,allowed,reason="no-continuous-readable-space",rejected,recordIds=group.Rows.Select(t=>t.RecordId).ToArray()});
            }
        }
        return result;

        bool Eligible(CadLayoutText t) => eligible.Contains(t.RecordId);
    }

    private static MText Probe(Database db,string target,double height,double width,string language)
    {
        var text=new MText(); text.SetDatabaseDefaults(db); text.Attachment=AttachmentPoint.TopLeft;
        text.TextHeight=height; text.Width=width;
        text.Contents=Contents(target,language);
        return text;
    }

    internal static IEnumerable<Rect2> Destinations(Rect2 source,double w,double h,double height,Rect2 allowed,Rect2? innerFrame)
    {
        foreach(double gap in new[]{height,3*height,6*height,10*height})
        {
            if(innerFrame is Rect2 inner)
            {
                yield return new(source.Left,inner.Top+gap,source.Left+w,inner.Top+gap+h);
                yield return new(source.Left,inner.Bottom-gap-h,source.Left+w,inner.Bottom-gap);
                yield return new(source.Center.X-w/2,inner.Top+gap,source.Center.X+w/2,inner.Top+gap+h);
                yield return new(source.Center.X-w/2,inner.Bottom-gap-h,source.Center.X+w/2,inner.Bottom-gap);
            }
            yield return new(source.Left,source.Bottom-gap-h,source.Left+w,source.Bottom-gap);
            yield return new(source.Left,source.Top+gap,source.Left+w,source.Top+gap+h);
            yield return new(source.Right+gap,source.Top-h,source.Right+gap+w,source.Top);
            yield return new(source.Left-gap-w,source.Top-h,source.Left-gap,source.Top);
            yield return new(source.Center.X-w/2,source.Bottom-gap-h,source.Center.X+w/2,source.Bottom-gap);
            yield return new(source.Left-height,source.Bottom-gap-h,source.Left-height+w,source.Bottom-gap);
            yield return new(source.Left+height,source.Bottom-gap-h,source.Left+height+w,source.Bottom-gap);
            yield return new(allowed.Left+gap,source.Bottom-gap-h,allowed.Left+gap+w,source.Bottom-gap);
            yield return new(allowed.Right-gap-w,source.Bottom-gap-h,allowed.Right-gap,source.Bottom-gap);
            yield return new(source.Left,allowed.Bottom+gap,source.Left+w,allowed.Bottom+gap+h);
            yield return new(source.Center.X-w/2,allowed.Bottom+gap,source.Center.X+w/2,allowed.Bottom+gap+h);
            yield return new(allowed.Left+gap,allowed.Bottom+gap,allowed.Left+gap+w,allowed.Bottom+gap+h);
            yield return new(allowed.Right-gap-w,allowed.Bottom+gap,allowed.Right-gap,allowed.Bottom+gap+h);
            yield return new(source.Right+gap,allowed.Top-gap-h,source.Right+gap+w,allowed.Top-gap);
            yield return new(source.Right+gap,allowed.Bottom+gap,source.Right+gap+w,allowed.Bottom+gap+h);
            yield return new(source.Left-gap-w,allowed.Top-gap-h,source.Left-gap,allowed.Top-gap);
            yield return new(source.Left-gap-w,allowed.Bottom+gap,source.Left-gap,allowed.Bottom+gap+h);
        }
    }
    private static Rect2 Point(Point2 p)=>new(p.X,p.Y,p.X,p.Y);
    private static Rect2 Union(IEnumerable<Rect2> boxes) {var b=boxes.ToArray();return new(b.Min(x=>x.Left),b.Min(x=>x.Bottom),b.Max(x=>x.Right),b.Max(x=>x.Top));}
    private static bool SegmentsCross(Segment2 a,Segment2 b)
    {
        double Cross(Point2 p,Point2 q,Point2 r)=>(q.X-p.X)*(r.Y-p.Y)-(q.Y-p.Y)*(r.X-p.X);
        return Cross(a.Start,a.End,b.Start)*Cross(a.Start,a.End,b.End)<0 && Cross(b.Start,b.End,a.Start)*Cross(b.Start,b.End,a.End)<0;
    }
}

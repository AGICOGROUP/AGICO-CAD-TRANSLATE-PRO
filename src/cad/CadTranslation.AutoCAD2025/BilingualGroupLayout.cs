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
        CadObjectAccess access, List<object> decisions,
        IReadOnlyDictionary<string,IReadOnlyList<Rect2[]>>? tableGroups = null, HashSet<string>? blocked = null,
        HashSet<string>? released = null)
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
            if(BilingualLabelEquivalence.MatchesInline(sourcePlain[t.RecordId],targetPlain[t.RecordId])) continue;
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
            // True tables belong exclusively to the complete-copy path. A
            // failed copy cannot silently become a text-only panel.
            // Only root model/paper tables enter the complete-copy path. Nested
            // diagram blocks can contain apparent grids formed by weld symbols;
            // their labels must remain eligible for nearby translation.
            bool completeCopyOwner=d.Name.StartsWith("*Model_Space",StringComparison.OrdinalIgnoreCase) ||
                d.Name.StartsWith("*Paper_Space",StringComparison.OrdinalIgnoreCase);
            foreach (var cells in completeCopyOwner ?
                tableGroups?.GetValueOrDefault(d.Name) ?? BilingualTableLayout.TranslationGroups(d.Regions,d.BoundarySegments) : [])
            {
                // Match the complete-copy table threshold. A single row of
                // weld-symbol boxes is a legend, not an uncopyable table.
                if (cells.Length < 3 || !BilingualTableLayout.HasRealColumns(cells) ||
                    cells.Select(c=>Math.Round(c.Center.Y,3)).Distinct().Count()<2) continue;
                foreach (var t in d.Texts.Where(t=>cells.Any(c=>c.Contains(Point(t.Source.Bounds.Center)))))
                {
                    tableIds.Add(t.RecordId);
                    // Members released by a failed complete copy keep ordinary
                    // in-place label placement instead of a grouped repair.
                    if (Eligible(t) && !(released?.Contains(t.RecordId) ?? false)) blocked?.Add(t.RecordId);
                }
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
                if (requests.Length < 2 || !BilingualReadingGroupPolicy.IsNarrative(requests.Select(t=>sourcePlain[t.RecordId]))) continue;
                groups.Add((requests, Union(narrative.MemberIds.Select(id=>d.Texts.First(t=>t.RecordId==id).Source.Bounds)), "note-block"));
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
                    int Number(CadLayoutText t) => int.TryParse(System.Text.RegularExpressions.Regex.Match(sourcePlain[t.RecordId],@"^\s*(\d+)[.．、)）]").Groups[1].Value,out int n) ? n : -1;
                    bool consecutiveNotes=Number(a)>=0 && Number(b)>=0 && Math.Abs(Number(a)-Number(b))==1 &&
                        BilingualReadingGroupPolicy.IsNarrative([sourcePlain[a.RecordId],sourcePlain[b.RecordId]]);
                    if (Math.Abs(a.Source.Bounds.Left-b.Source.Bounds.Left)>h*.8 || gap>h*(consecutiveNotes?8:1.8) ||
                        Math.Abs(a.Source.Bounds.Center.Y-b.Source.Bounds.Center.Y)<h*.5 ||
                        Math.Min(a.Source.OriginalTextHeight,b.Source.OriginalTextHeight)<h*.7 ||
                        byId[a.RecordId].Manifest.Properties.Layer!=byId[b.RecordId].Manifest.Properties.Layer) continue;
                    var connector=new Segment2(a.Source.Bounds.Center,b.Source.Bounds.Center);
                    if(d.BoundarySegments.Any(s=>SegmentsCross(connector,s))) continue;
                    members.Add(b); remaining.RemoveAt(j);
                }
                var requests=members.Where(Eligible).OrderByDescending(t=>t.Source.Bounds.Center.Y).ThenBy(t=>t.Source.Bounds.Left).ToArray();
                bool paragraph=requests.Length==1 && (sourcePlain[requests[0].RecordId].Length>=100 ||
                    sourcePlain[requests[0].RecordId].Count(c=>c is >= '\u3400' and <= '\u9fff')>=30 &&
                    requests[0].Source.Bounds.Height>=requests[0].Source.OriginalTextHeight*1.5);
                if(requests.Length>=3 || paragraph || requests.Length==2 && BilingualReadingGroupPolicy.IsNarrative(requests.Select(t=>sourcePlain[t.RecordId]))) groups.Add((requests,Union(members.Select(t=>t.Source.Bounds)),
                    paragraph || BilingualReadingGroupPolicy.IsNarrative(requests.Select(t=>sourcePlain[t.RecordId])) ? "note-block" : "legend-rows"));
            }

            var frameRegions=d.Regions.Where(r=>r.Kind==LayoutRegionKind.ClosedFrame).ToList();
            foreach(var other in baseline.Definitions.Where(other=>other.Name!=d.Name))
            foreach(var region in other.Regions.Where(r=>r.Kind==LayoutRegionKind.ClosedFrame))
                frameRegions.AddRange(InstanceOccupancyProjection.Project(region.Bounds,other.Name,d.Name,baseline.BlockInstances)
                    .Select(b=>new LayoutRegion("projected-group-frame",LayoutRegionKind.ClosedFrame,b)));
            foreach(var group in groups.OrderByDescending(g=>g.Source.Area))
            {
                double sourceHeight=group.Rows.Min(t=>t.Source.OriginalTextHeight);
                var groupFrames=frameRegions.Concat(BilingualTableCopy.DetectFrames(d.BoundarySegments,group.Source)
                    .Select(b=>new LayoutRegion("prose-frame",LayoutRegionKind.ClosedFrame,b))).DistinctBy(r=>r.Bounds).ToArray();
                var frame=groupFrames.Where(r=>r.Bounds.Contains(group.Source) && r.Bounds.Area>group.Source.Area*1.1)
                    .OrderBy(r=>r.Bounds.Area).FirstOrDefault();
                var containingFrames=groupFrames.Where(r=>r.Bounds.Contains(group.Source) && r.Bounds.Area>group.Source.Area*1.1)
                    .Select(r=>r.Bounds).Distinct().ToArray();
                if(group.Strategy=="legend-rows")
                {
                    PlanLegend(db,baseline,d,group.Rows,group.Source,frame?.Bounds,targetPlain,occupied,language,result,decisions,blocked);
                    continue;
                }
                var display=group.Rows.Select(row=>targetPlain[row.RecordId]).ToArray();
                bool placed=false;
                var rejected=new List<object>();
                // One fixed-width column. Translation growth changes height,
                // never the block width or its reading order.
                double width=group.Source.Width/CadLayoutGeometry.MTextMeasurementSafetyScale;
                foreach(double scale in new[]{1.0,.9,.8,.7})
                {
                    double height=sourceHeight*scale, gap=height*.45;
                    var sizes=new List<Rect2>();
                    foreach(var value in display)
                    {
                        using var probe=Probe(db,value,height,width,language);
                        sizes.Add(BilingualPlacementChecks.Footprint(probe));
                    }
                    if(sizes.Any(b=>b.Width>group.Source.Width+1e-5)) continue;
                    double total=sizes.Sum(b=>b.Height)+gap*(sizes.Count-1);
                    // Side and vertical neighbours compete on distance, so a long
                    // translation sits just above or below its source instead of
                    // far off to one side.
                    var panelFrames=groupFrames.Select(r=>r.Bounds).ToArray();
                    foreach(var destination in BilingualPanelPlacement.SideDestinations(group.Source,total,sourceHeight,
                        panelFrames,occupied[d.Name])
                        .Concat(BilingualPanelPlacement.VerticalDestinations(group.Source,total,sourceHeight,
                        panelFrames,occupied[d.Name]))
                        .OrderBy(b=>BilingualLocalPlacement.Gap(group.Source,b)))
                    {
                        var conflict=occupied[d.Name].FirstOrDefault(b=>BilingualDrawingImporter.Intersects(destination,b,height*.2));
                        if(conflict!=default)
                        {
                            if(rejected.Count<20)rejected.Add(new{height,width,destination,text=conflict});
                            continue;
                        }
                        double top=destination.Top;
                        for(int i=0;i<group.Rows.Length;i++)
                        {
                            var slot=new Rect2(destination.Left,top-sizes[i].Height,destination.Right,top);
                            result[group.Rows[i].RecordId]=new(slot,height,width,"note-block",display[i]);
                            top-=sizes[i].Height+gap;
                        }
                        occupied[d.Name].Add(destination);
                        foreach(var name in occupied.Keys.Where(n=>n!=d.Name))
                            occupied[name].AddRange(InstanceOccupancyProjection.Project(destination,d.Name,name,baseline.BlockInstances));
                        decisions.Add(new{strategy="note-block",source=group.Source,destination,recordIds=group.Rows.Select(t=>t.RecordId).ToArray(),height,columns=1,
                            outsideContainingFrame=frame is not null && !containingFrames.Any(f=>f.Contains(destination))});
                        placed=true;break;
                    }
                    if(placed) break;
                }
                if(!placed)
                {
                    // Keep the grouped attempt auditable, but never strand the
                    // rows: each falls back to its own nearby label placement.
                    decisions.Add(new{strategy=group.Strategy,source=group.Source,reason="no-continuous-readable-side-space",rejected,recordIds=group.Rows.Select(t=>t.RecordId).ToArray()});
                }
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

    private static void PlanLegend(Database db,CadLayoutBaseline baseline,CadDefinitionTopology definition,
        CadLayoutText[] rows,Rect2 source,Rect2? frame,IReadOnlyDictionary<string,string> targets,
        Dictionary<string,List<Rect2>> occupied,string language,Dictionary<string,Slot> result,List<object> decisions,HashSet<string>? blocked)
    {
        double h=rows.Min(r=>r.Source.OriginalTextHeight);
        foreach(double scale in new[]{.85,.7})
        {
            var sizes=new List<Rect2>();
            foreach(var row in rows)
            {
                using var probe=Probe(db,targets[row.RecordId],h*scale,0,language);
                sizes.Add(BilingualPlacementChecks.Footprint(probe));
            }
            double width=sizes.Max(s=>s.Width),gap=h*.4;
            // One common column with original row centers, not a reflowed paragraph.
            foreach(bool right in new[]{true,false})
            {
                double left=right?source.Right+gap:source.Left-gap-width;
                var slots=rows.Select((r,i)=>new Rect2(left,r.Source.Bounds.Center.Y-sizes[i].Height/2,
                    left+sizes[i].Width,r.Source.Bounds.Center.Y+sizes[i].Height/2)).ToArray();
                if(slots.Where((s,i)=>BilingualLocalPlacement.Gap(s,rows[i].Source.Bounds)>h*4).Any()) continue;
                if(slots.Any(s=>occupied[definition.Name].Any(b=>BilingualDrawingImporter.Intersects(s,b,h*.12)))) continue;
                if(slots.Where((s,i)=>slots.Take(i).Any(b=>BilingualDrawingImporter.Intersects(s,b,h*.12))).Any()) continue;
                for(int i=0;i<rows.Length;i++)
                {
                    result[rows[i].RecordId]=new(slots[i],h*scale,0,"legend-rows",targets[rows[i].RecordId]);
                    occupied[definition.Name].Add(slots[i]);
                    foreach(var name in occupied.Keys.Where(n=>n!=definition.Name))
                        occupied[name].AddRange(InstanceOccupancyProjection.Project(slots[i],definition.Name,name,baseline.BlockInstances));
                }
                decisions.Add(new{strategy="legend-rows",source,destination=Union(slots),recordIds=rows.Select(r=>r.RecordId).ToArray(),height=h*scale});
                return;
            }
        }
        // An unplaceable legend keeps its audit record; rows fall back to
        // individual nearby labels instead of missing their translations.
        decisions.Add(new{strategy="legend-rows-unresolved",source,reason="no-nearby-aligned-column",recordIds=rows.Select(r=>r.RecordId).ToArray()});
    }

    internal static IEnumerable<Rect2> Destinations(Rect2 source,double w,double h,double height,Rect2 allowed,Rect2? innerFrame)
        => BilingualPanelPlacement.Destinations(source,w,h,height,allowed,innerFrame);
    private static Rect2 Point(Point2 p)=>new(p.X,p.Y,p.X,p.Y);
    private static Rect2 Union(IEnumerable<Rect2> boxes) {var b=boxes.ToArray();return new(b.Min(x=>x.Left),b.Min(x=>x.Bottom),b.Max(x=>x.Right),b.Max(x=>x.Top));}
    private static bool SegmentsCross(Segment2 a,Segment2 b)
    {
        double Cross(Point2 p,Point2 q,Point2 r)=>(q.X-p.X)*(r.Y-p.Y)-(q.Y-p.Y)*(r.X-p.X);
        return Cross(a.Start,a.End,b.Start)*Cross(a.Start,a.End,b.End)<0 && Cross(b.Start,b.End,a.Start)*Cross(b.Start,b.End,a.End)<0;
    }
}

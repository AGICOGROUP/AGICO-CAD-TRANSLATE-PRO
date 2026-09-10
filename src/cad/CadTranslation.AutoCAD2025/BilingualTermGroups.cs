using CadTranslation.Contracts;
using CadTranslation.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace CadTranslation.AutoCAD2025;

// A translation group is not a mutation of the original CAD entities.
internal static class BilingualTermGroups
{
    internal sealed record Group(string SourceText, string[] RecordIds);

    internal static Group[] Find(IReadOnlyList<ManifestRecord> records, CadLayoutBaseline? baseline = null, CadObjectAccess? access = null)
    {
        var result = new List<Group>();
        var eligible = records.Where(r => r.PlainText.Length == 1 && r.PlainText[0] is >= '\u3400' and <= '\u9fff' &&
            r.ProtectedTokens.Count == 0 && r.ObjectType is "AcDbText" or "AcDbMText" &&
            Math.Abs(r.Geometry.RotationRadians) < 1e-6 && r.Properties.Height > 0).ToArray();
        foreach (var owner in eligible.GroupBy(r => (r.OwnerPath, r.Properties.Layer, r.Properties.TextStyle)))
        {
            var rows = owner.ToArray();
            var used = new HashSet<string>();
            // Reading order: top-to-bottom or left-to-right; ambiguous grids stay separate.
            foreach (bool vertical in new[] { true, false })
            {
                var next = new Dictionary<string, ManifestRecord>();
                foreach (var a in rows)
                {
                    var candidates = rows.Where(b => a.RecordId != b.RecordId && Adjacent(a,b,vertical))
                        .OrderBy(b => Distance(a,b,vertical)).ToArray();
                    if (candidates.Length > 0 && (candidates.Length == 1 ||
                        Distance(a,candidates[1],vertical)-Distance(a,candidates[0],vertical)>a.Properties.Height*.20))
                        next[a.RecordId] = candidates[0];
                }
                foreach (var start in rows.Where(a => !next.Values.Any(b => b.RecordId == a.RecordId)))
                {
                    var chain = new List<ManifestRecord> { start };
                    while (next.TryGetValue(chain[^1].RecordId, out var tail) && chain.Count <= 12)
                        chain.Add(tail);
                    if (chain.Count is < 2 or > 12 || chain.Any(r => used.Contains(r.RecordId))) continue;
                    if (chain.Any(a => rows.Any(b => a.RecordId != b.RecordId &&
                        (Adjacent(a,b,!vertical) || Adjacent(b,a,!vertical))))) continue;
                    if (baseline is not null && !ClearBoundaries(chain,baseline,access)) continue;
                    result.Add(new(string.Concat(chain.Select(r=>r.PlainText)), chain.Select(r=>r.RecordId).ToArray()));
                    foreach (var row in chain) used.Add(row.RecordId);
                }
            }
        }
        return result.ToArray();
    }

    private static bool Adjacent(ManifestRecord a, ManifestRecord b, bool vertical)
    {
        double h=a.Properties.Height;
        if (Math.Abs(b.Properties.Height-h)>h*.12) return false;
        var p=a.Geometry.InsertionPoint; var q=b.Geometry.InsertionPoint;
        if (Math.Abs(p.Z-q.Z)>h*.02) return false;
        double along=vertical ? p.Y-q.Y : q.X-p.X;
        double across=vertical ? p.X-q.X : p.Y-q.Y;
        return Math.Abs(across)<=h*.20 && along>=h*.65 && along<=h*2.2;
    }

    private static double Distance(ManifestRecord a, ManifestRecord b, bool vertical) => vertical
        ? a.Geometry.InsertionPoint.Y-b.Geometry.InsertionPoint.Y
        : b.Geometry.InsertionPoint.X-a.Geometry.InsertionPoint.X;

    private static bool ClearBoundaries(IReadOnlyList<ManifestRecord> rows, CadLayoutBaseline baseline, CadObjectAccess? access)
    {
        var texts=baseline.Definitions.SelectMany(d=>d.Texts).ToDictionary(t=>t.RecordId);
        if (rows.Any(r=>!texts.ContainsKey(r.RecordId))) return false;
        var members=rows.Select(r=>texts[r.RecordId]).ToArray();
        if (members.Any(t=>t.DefinitionName!=members[0].DefinitionName || t.Region?.Id!=members[0].Region?.Id)) return false;
        foreach (var member in members)
        {
            var entity=access?.Read<Entity>(member.ObjectId,"term-group-plane",recordId:member.RecordId);
            var normal=entity switch {DBText text=>text.Normal,MText text=>text.Normal,_=>new Vector3d(0,0,0)};
            if (!normal.IsEqualTo(Vector3d.ZAxis)) return false;
        }
        var definition=baseline.Definitions.Single(d=>d.Name==members[0].DefinitionName);
        var memberIds=rows.Select(r=>r.RecordId).ToHashSet();
        for (int i=1;i<members.Length;i++)
        {
            var a=members[i-1].Source.Bounds.Center; var b=members[i].Source.Bounds.Center;
            double margin=rows[i].Properties.Height*.02;
            var corridor=new Rect2(Math.Min(a.X,b.X)-margin,Math.Min(a.Y,b.Y)-margin,Math.Max(a.X,b.X)+margin,Math.Max(a.Y,b.Y)+margin);
            if (definition.BoundarySegments.Any(s=>BilingualDrawingImporter.Crosses(corridor,s))) return false;
            if (definition.Texts.Any(t=>!memberIds.Contains(t.RecordId) &&
                BilingualDrawingImporter.Intersects(corridor,t.Source.Bounds,0))) return false;
        }
        return true;
    }
}

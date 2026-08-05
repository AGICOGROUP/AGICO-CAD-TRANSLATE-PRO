namespace CadTranslation.Core;

public sealed record FragmentedNarrativeSample(
    string Id,
    Rect2 SourceBounds,
    string SourceText,
    bool IsEligible);

public sealed record FragmentedNarrativeGroup(
    IReadOnlyList<string> MemberIds,
    Rect2 SourceBounds,
    int RowCount,
    double FragmentationRatio,
    int CjkCount);

public static class FragmentedNarrativeDetector
{
    public static FragmentedNarrativeGroup[] DetectGroups(
        IReadOnlyList<FragmentedNarrativeSample> samples,
        double medianTextHeight,
        double tolerance = 1e-6)
    {
        ArgumentNullException.ThrowIfNull(samples);
        double height = Math.Max(medianTextHeight, tolerance);
        FragmentedNarrativeSample[] eligible = samples
            .Where(sample => sample.IsEligible && !string.IsNullOrWhiteSpace(sample.SourceText))
            .ToArray();
        if (eligible.Length == 0)
        {
            return [];
        }

        IReadOnlyDictionary<string, FragmentedNarrativeSample> byId = eligible
            .ToDictionary(sample => sample.Id, StringComparer.Ordinal);
        NarrativeLogicalRow[] visualRows = NarrativeRowPlanner.Group(
            eligible.Select(sample => new NarrativeRowItem(sample.Id, sample.SourceBounds)).ToArray(),
            height,
            tolerance);

        var lines = new List<LogicalLine>();
        for (int rowIndex = 0; rowIndex < visualRows.Length; rowIndex++)
        {
            NarrativeLogicalRow row = visualRows[rowIndex];
            NarrativeRowCluster[] clusters = NarrativeRowClusterPlanner.Partition(
                row.MemberIds.Select(id => new NarrativeRowItem(id, byId[id].SourceBounds)).ToArray(),
                height,
                tolerance);
            lines.AddRange(clusters.Select(cluster => new LogicalLine(
                rowIndex,
                cluster.MemberIds,
                cluster.SourceBounds)));
        }

        var disjoint = new DisjointSet(lines.Count);
        for (int left = 0; left < lines.Count; left++)
        {
            for (int right = left + 1; right < lines.Count; right++)
            {
                if (lines[left].RowIndex == lines[right].RowIndex)
                {
                    continue;
                }

                LogicalLine upper = lines[left].SourceBounds.Center.Y >= lines[right].SourceBounds.Center.Y
                    ? lines[left]
                    : lines[right];
                LogicalLine lower = ReferenceEquals(upper, lines[left]) ? lines[right] : lines[left];
                double verticalGap = Math.Max(0, upper.SourceBounds.Bottom - lower.SourceBounds.Top);
                double startDrift = Math.Abs(upper.SourceBounds.Left - lower.SourceBounds.Left);
                if (verticalGap <= height * 3.5 + tolerance &&
                    startDrift <= height * 5 + tolerance)
                {
                    disjoint.Union(left, right);
                }
            }
        }

        return lines
            .Select((line, index) => (line, root: disjoint.Find(index)))
            .GroupBy(value => value.root)
            .Select(component => BuildGroup(component.Select(value => value.line).ToArray(), byId))
            .Where(group => IsHighConfidence(
                group.MemberIds.Count,
                group.RowCount,
                group.CjkCount,
                group.MemberIds.Sum(id => VisibleLength(byId[id].SourceText))))
            .OrderByDescending(group => group.SourceBounds.Top)
            .ThenBy(group => group.SourceBounds.Left)
            .ToArray();
    }

    public static bool IsHighConfidence(
        int memberCount,
        int rowCount,
        int cjkCount,
        int visibleSourceCharacters)
    {
        if (rowCount < 6 || memberCount < 12)
        {
            return false;
        }

        double fragmentation = memberCount / (double)rowCount;
        double averageCharactersPerRow = visibleSourceCharacters / (double)rowCount;
        bool fragmentedProse =
            fragmentation >= 1.5 &&
            cjkCount >= 100 &&
            averageCharactersPerRow >= 8;
        bool continuousProse =
            rowCount >= 15 &&
            memberCount >= 15 &&
            cjkCount >= 200 &&
            averageCharactersPerRow >= 10;
        return fragmentedProse || continuousProse;
    }

    public static int CountCjk(string value) => value.Count(character =>
        character is >= '\u3400' and <= '\u4dbf' or
        >= '\u4e00' and <= '\u9fff' or
        >= '\uf900' and <= '\ufaff');

    public static int VisibleLength(string value) =>
        value.Count(character => !char.IsWhiteSpace(character));

    private static FragmentedNarrativeGroup BuildGroup(
        IReadOnlyList<LogicalLine> lines,
        IReadOnlyDictionary<string, FragmentedNarrativeSample> byId)
    {
        string[] memberIds = lines
            .SelectMany(line => line.MemberIds)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(id => byId[id].SourceBounds.Center.Y)
            .ThenBy(id => byId[id].SourceBounds.Left)
            .ThenBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Rect2 bounds = Union(lines.Select(line => line.SourceBounds));
        int rowCount = lines.Select(line => line.RowIndex).Distinct().Count();
        int cjkCount = memberIds.Sum(id => CountCjk(byId[id].SourceText));
        return new FragmentedNarrativeGroup(
            memberIds,
            bounds,
            rowCount,
            memberIds.Length / (double)rowCount,
            cjkCount);
    }

    private static Rect2 Union(IEnumerable<Rect2> values)
    {
        Rect2[] bounds = values.ToArray();
        return new Rect2(
            bounds.Min(value => value.Left),
            bounds.Min(value => value.Bottom),
            bounds.Max(value => value.Right),
            bounds.Max(value => value.Top));
    }

    private sealed record LogicalLine(
        int RowIndex,
        IReadOnlyList<string> MemberIds,
        Rect2 SourceBounds);

    private sealed class DisjointSet
    {
        private readonly int[] _parents;

        public DisjointSet(int count) => _parents = Enumerable.Range(0, count).ToArray();

        public int Find(int value)
        {
            if (_parents[value] != value)
            {
                _parents[value] = Find(_parents[value]);
            }
            return _parents[value];
        }

        public void Union(int left, int right)
        {
            int leftRoot = Find(left);
            int rightRoot = Find(right);
            if (leftRoot != rightRoot)
            {
                _parents[rightRoot] = leftRoot;
            }
        }
    }
}

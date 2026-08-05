namespace CadTranslation.Core;

public static class FragmentedNarrativeTableRowFilter
{
    public static string[] KeepNarrativeMembers(
        IReadOnlyList<FragmentedNarrativeSample> samples,
        double medianTextHeight,
        double tolerance = 1e-6)
    {
        ArgumentNullException.ThrowIfNull(samples);
        FragmentedNarrativeSample[] eligible = samples
            .Where(sample => sample.IsEligible)
            .ToArray();
        IReadOnlyDictionary<string, FragmentedNarrativeSample> byId = eligible
            .ToDictionary(sample => sample.Id, StringComparer.Ordinal);
        NarrativeLogicalRow[] rows = NarrativeRowPlanner.Group(
            eligible.Select(sample => new NarrativeRowItem(sample.Id, sample.SourceBounds)).ToArray(),
            medianTextHeight,
            tolerance);

        var kept = new List<string>();
        foreach (NarrativeLogicalRow row in rows)
        {
            int[] lengths = row.MemberIds
                .Select(id => FragmentedNarrativeDetector.VisibleLength(byId[id].SourceText))
                .ToArray();
            int total = lengths.Sum();
            bool distributedCells = lengths.Length >= 3 && total > 0 &&
                lengths.Max() / (double)total <= 0.65;
            if (distributedCells)
            {
                continue;
            }

            kept.AddRange(row.MemberIds
                .OrderBy(id => byId[id].SourceBounds.Left)
                .ThenBy(id => id, StringComparer.Ordinal));
        }
        return kept.ToArray();
    }
}

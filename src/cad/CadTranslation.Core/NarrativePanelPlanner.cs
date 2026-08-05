namespace CadTranslation.Core;

public static class NarrativePanelPlanner
{
    public static FragmentedNarrativeGroup[] SelectCompletePanelColumns(
        IReadOnlyList<FragmentedNarrativeGroup> seedGroups,
        IReadOnlyList<FragmentedNarrativeGroup> secondPassGroups,
        double medianTextHeight,
        double tolerance = 1e-6)
    {
        ArgumentNullException.ThrowIfNull(seedGroups);
        ArgumentNullException.ThrowIfNull(secondPassGroups);
        FragmentedNarrativeGroup[] seeds = seedGroups.ToArray();
        if (!AreDistinctColumns(seeds, medianTextHeight, tolerance))
        {
            return StretchToSharedVerticalEnvelope(seeds, medianTextHeight, tolerance);
        }

        double height = Math.Max(medianTextHeight, tolerance);
        var selected = new List<FragmentedNarrativeGroup>(seeds);
        foreach (FragmentedNarrativeGroup candidate in secondPassGroups)
        {
            bool belongsToExistingColumn = seeds.Any(seed =>
                candidate.SourceBounds.Center.X >= seed.SourceBounds.Left - height * 2 - tolerance &&
                candidate.SourceBounds.Center.X <= seed.SourceBounds.Right + height * 2 + tolerance);
            if (!belongsToExistingColumn)
            {
                selected.Add(candidate);
            }
        }

        return StretchToSharedVerticalEnvelope(selected, medianTextHeight, tolerance);
    }

    public static FragmentedNarrativeGroup[] StretchToSharedVerticalEnvelope(
        IReadOnlyList<FragmentedNarrativeGroup> groups,
        double medianTextHeight,
        double tolerance = 1e-6)
    {
        ArgumentNullException.ThrowIfNull(groups);
        FragmentedNarrativeGroup[] original = groups.ToArray();
        if (original.Length < 3)
        {
            return original;
        }

        if (!AreDistinctColumns(original, medianTextHeight, tolerance))
        {
            return original;
        }

        double top = original.Max(group => group.SourceBounds.Top);
        double bottom = original.Min(group => group.SourceBounds.Bottom);
        return original.Select(group => group with
            {
                SourceBounds = new Rect2(
                    group.SourceBounds.Left,
                    bottom,
                    group.SourceBounds.Right,
                    top)
            })
            .ToArray();
    }

    private static bool AreDistinctColumns(
        IReadOnlyList<FragmentedNarrativeGroup> groups,
        double medianTextHeight,
        double tolerance)
    {
        if (groups.Count < 3)
        {
            return false;
        }

        double height = Math.Max(medianTextHeight, tolerance);
        FragmentedNarrativeGroup[] ordered = groups
            .OrderBy(group => group.SourceBounds.Center.X)
            .ToArray();
        return ordered
            .Zip(ordered.Skip(1), (left, right) =>
                right.SourceBounds.Center.X - left.SourceBounds.Center.X > height * 8 + tolerance)
            .All(value => value);
    }
}

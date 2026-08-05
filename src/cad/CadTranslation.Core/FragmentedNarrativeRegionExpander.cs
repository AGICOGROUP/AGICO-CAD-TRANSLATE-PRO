namespace CadTranslation.Core;

public static class FragmentedNarrativeRegionExpander
{
    public static string[] Expand(
        FragmentedNarrativeGroup seed,
        IReadOnlyList<FragmentedNarrativeSample> samples,
        double medianTextHeight,
        double tolerance = 1e-6)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(samples);
        IReadOnlyDictionary<string, FragmentedNarrativeSample> byId = samples
            .ToDictionary(sample => sample.Id, StringComparer.Ordinal);
        FragmentedNarrativeSample[] seedSamples = seed.MemberIds
            .Where(byId.ContainsKey)
            .Select(id => byId[id])
            .ToArray();
        if (seedSamples.Length == 0)
        {
            return [];
        }

        double height = Math.Max(medianTextHeight, tolerance);
        double left = Percentile(seedSamples.Select(sample => sample.SourceBounds.Left), 0.05);
        double right = Percentile(seedSamples.Select(sample => sample.SourceBounds.Right), 0.95);
        double horizontalPadding = height * 0.50;
        double verticalPadding = height * 0.50;

        return samples
            .Where(sample => sample.IsEligible &&
                sample.SourceBounds.Center.X >= left - horizontalPadding - tolerance &&
                sample.SourceBounds.Center.X <= right + horizontalPadding + tolerance &&
                sample.SourceBounds.Center.Y >= seed.SourceBounds.Bottom - verticalPadding - tolerance &&
                sample.SourceBounds.Center.Y <= seed.SourceBounds.Top + verticalPadding + tolerance)
            .OrderByDescending(sample => sample.SourceBounds.Center.Y)
            .ThenBy(sample => sample.SourceBounds.Left)
            .ThenBy(sample => sample.Id, StringComparer.Ordinal)
            .Select(sample => sample.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        double[] ordered = values.OrderBy(value => value).ToArray();
        int index = (int)Math.Round((ordered.Length - 1) * percentile);
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }
}

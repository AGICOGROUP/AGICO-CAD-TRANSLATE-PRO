namespace CadTranslation.Core;

public static class AuthoritativeNarrativeSelector
{
    public static NarrativeOccupancyGroup[] SelectPanelGroups(
        IReadOnlyList<FragmentedNarrativeSample> samples,
        double medianTextHeight,
        double tolerance = 1e-6)
    {
        ArgumentNullException.ThrowIfNull(samples);
        IReadOnlyDictionary<string, FragmentedNarrativeSample> byId = samples
            .ToDictionary(sample => sample.Id, StringComparer.Ordinal);
        NarrativeRowItem[][] panels = NarrativeHorizontalPanelPartitioner.Partition(
            samples.Select(sample => new NarrativeRowItem(sample.Id, sample.SourceBounds)).ToArray(),
            medianTextHeight);
        return panels
            .SelectMany(panel => SelectGroups(
                panel.Select(item => byId[item.Id]).ToArray(),
                medianTextHeight,
                tolerance))
            .ToArray();
    }

    public static NarrativeOccupancyGroup[] SelectGroups(
        IReadOnlyList<FragmentedNarrativeSample> samples,
        double medianTextHeight,
        double tolerance = 1e-6)
    {
        ArgumentNullException.ThrowIfNull(samples);
        return NarrativeOccupancyDetector.DetectGroups(
            samples
                .Where(sample => sample.IsEligible && IsNarrativeCandidate(sample.SourceText))
                .Select(sample => new NarrativeOccupancySample(
                    sample.Id,
                    new Point2(sample.SourceBounds.Left, sample.SourceBounds.Top),
                    sample.SourceBounds,
                    sample.SourceText,
                    true))
                .ToArray(),
            medianTextHeight,
            tolerance);
    }

    private static bool IsNarrativeCandidate(string sourceText)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return false;
        }

        string visible = new(sourceText.Where(character => !char.IsWhiteSpace(character)).ToArray());
        if (visible.Length >= 10)
        {
            return true;
        }

        if (visible.Length < 4 || !char.IsDigit(visible[0]))
        {
            return false;
        }

        int separator = visible.IndexOfAny(['.', '．', '、', ')', '）']);
        return separator is > 0 and <= 4 &&
               visible[(separator + 1)..].Any(character =>
                   char.IsLetter(character) ||
                   character is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff');
    }
}

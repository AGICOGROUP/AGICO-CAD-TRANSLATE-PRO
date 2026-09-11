using System.Text;
using System.Text.RegularExpressions;

namespace CadTranslation.Core;

public sealed record BilingualNarrativeSample(
    string Id,
    string Text,
    string ObjectType);

public sealed record BilingualNarrativeSelection(
    bool PreferExistingEnglish,
    IReadOnlyList<string> ExistingEnglishIds,
    IReadOnlyList<string> DuplicateChineseNarrativeIds);

public static class BilingualNarrativePolicy
{
    public static BilingualNarrativeSelection Select(
        IReadOnlyList<BilingualNarrativeSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        BilingualNarrativeSample[] chineseNarrative = samples
            .Where(sample => CountCjk(sample.Text) >= 6 && VisibleLength(sample.Text) >= 10)
            .ToArray();
        BilingualNarrativeSample[] existingEnglish = samples
            .Where(sample => sample.ObjectType == "AcDbMText" &&
                CountCjk(sample.Text) == 0 &&
                CountLatinLetters(sample.Text) >= 80)
            .ToArray();

        bool prefer = chineseNarrative.Length >= 2 &&
            chineseNarrative.Sum(sample => CountCjk(sample.Text)) >= 20 &&
            existingEnglish.Length > 0;
        return prefer
            ? new BilingualNarrativeSelection(
                true,
                existingEnglish.Select(sample => sample.Id).ToArray(),
                chineseNarrative.Select(sample => sample.Id).ToArray())
            : new BilingualNarrativeSelection(false, [], []);
    }

    private static int CountCjk(string value) => value.Count(character =>
        character is >= '\u3400' and <= '\u9fff');

    private static int CountLatinLetters(string value) => value.Count(character =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');

    private static int VisibleLength(string value) => value.Count(character =>
        !char.IsWhiteSpace(character) && character is not '{' and not '}');
}

public static class LogicalCompositionFitPolicy
{
    public static bool ShouldReplace(double actualHeight, double availableHeight) =>
        actualHeight > 0 &&
        availableHeight > 0 &&
        actualHeight <= availableHeight * 0.98;
}

public static class LogicalCompositionPlacementPolicy
{
    public static Rect2 SelectVerticalEnvelope(
        Rect2 sourceEnvelope,
        IReadOnlyList<Rect2> currentBounds,
        int expectedCount)
    {
        ArgumentNullException.ThrowIfNull(currentBounds);
        if (expectedCount <= 0 || currentBounds.Count != expectedCount)
        {
            return sourceEnvelope;
        }

        return new Rect2(
            sourceEnvelope.Left,
            currentBounds.Min(bounds => bounds.Bottom),
            sourceEnvelope.Right,
            currentBounds.Max(bounds => bounds.Top));
    }
}

public sealed record LogicalTextFragment(
    string Id,
    Rect2 SourceBounds,
    string Text);

public sealed record LogicalComposedRow(
    IReadOnlyList<string> MemberIds,
    Rect2 SourceBounds,
    string Text,
    bool ParagraphGapBefore);

public static partial class LogicalTextComposer
{
    public static LogicalComposedRow[] ComposeRows(
        IReadOnlyList<LogicalTextFragment> fragments,
        double medianTextHeight,
        double tolerance = 1e-6)
    {
        ArgumentNullException.ThrowIfNull(fragments);
        if (fragments.Count == 0)
        {
            return [];
        }

        IReadOnlyDictionary<string, LogicalTextFragment> byId = fragments
            .ToDictionary(fragment => fragment.Id, StringComparer.Ordinal);
        NarrativeLogicalRow[] sourceRows = NarrativeRowPlanner.Group(
            fragments.Select(fragment => new NarrativeRowItem(
                fragment.Id,
                fragment.SourceBounds)).ToArray(),
            medianTextHeight,
            tolerance);

        var result = new List<LogicalComposedRow>(sourceRows.Length);
        NarrativeLogicalRow? previous = null;
        foreach (NarrativeLogicalRow sourceRow in sourceRows)
        {
            LogicalTextFragment[] ordered = sourceRow.MemberIds
                .Select(id => byId[id])
                .OrderBy(fragment => fragment.SourceBounds.Left)
                .ThenBy(fragment => fragment.Id, StringComparer.Ordinal)
                .ToArray();
            string text = ComposeLine(ordered.Select(fragment => fragment.Text));
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            bool paragraphGap = previous is not null &&
                previous.SourceBounds.Bottom - sourceRow.SourceBounds.Top >
                Math.Max(medianTextHeight, tolerance) * 1.75 + tolerance;
            result.Add(new LogicalComposedRow(
                ordered.Select(fragment => fragment.Id).ToArray(),
                sourceRow.SourceBounds,
                text,
                paragraphGap));
            previous = sourceRow;
        }

        return result.ToArray();
    }

    public static string ComposeLine(IEnumerable<string> fragments)
    {
        var tokens = new List<string>();
        foreach (string value in fragments)
        {
            string token = NormalizePunctuation(value).Trim();
            if (token.Length == 0)
            {
                continue;
            }

            if (tokens.Count > 0 &&
                string.Equals(tokens[^1], token, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (tokens.Count > 0 &&
                string.Equals(token, "Year", StringComparison.OrdinalIgnoreCase) &&
                FourDigitYear().IsMatch(tokens[^1]))
            {
                continue;
            }

            tokens.Add(token);
        }

        var result = new StringBuilder();
        foreach (string token in tokens)
        {
            if (result.Length > 0 &&
                !EndsWithOpeningPunctuation(result) &&
                !StartsWithClosingPunctuation(token) &&
                !EndsWithTightSymbol(result) &&
                !StartsWithTightSymbol(token))
            {
                result.Append(' ');
            }

            result.Append(token);
        }

        return result.ToString();
    }

    private static string NormalizePunctuation(string value) => value
        .Replace('，', ',')
        .Replace('。', '.')
        .Replace('；', ';')
        .Replace('：', ':')
        .Replace('！', '!')
        .Replace('？', '?')
        .Replace('（', '(')
        .Replace('）', ')')
        .Replace('【', '[')
        .Replace('】', ']');

    private static bool EndsWithOpeningPunctuation(StringBuilder value) =>
        value[^1] is '(' or '[' or '{';

    private static bool StartsWithClosingPunctuation(string value) =>
        value[0] is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '%';

    private static bool EndsWithTightSymbol(StringBuilder value) =>
        value.Length >= 2 && value.ToString(value.Length - 2, 2) is "<<" or ">>";

    private static bool StartsWithTightSymbol(string value) =>
        value.StartsWith("<<", StringComparison.Ordinal) ||
        value.StartsWith(">>", StringComparison.Ordinal);

    [GeneratedRegex("^[12][0-9]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex FourDigitYear();
}

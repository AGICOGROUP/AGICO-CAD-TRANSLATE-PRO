using System.Text;
using System.Text.RegularExpressions;

namespace CadTranslation.Core;

public sealed record BilingualFixedLabelSample(
    string Id,
    string SourceText,
    string TranslatedText,
    string ObjectType,
    string DefinitionName,
    Rect2 SourceBounds,
    double SourceHeight);

public sealed record BilingualFixedLabelSelection(
    IReadOnlyList<string> SuppressChineseIds,
    IReadOnlyDictionary<string, string> MixedObjectEnglishTextById,
    IReadOnlyDictionary<string, string> ExistingEnglishIdBySourceId);

public static partial class BilingualFixedLabelPolicy
{
    public static bool ContainsEmbeddedEnglish(string source) =>
        IsDrawingNumberPair(source) || TryKeepEmbeddedEnglish(source, out _);

    private static bool IsDrawingNumberPair(string source)
    {
        string visible = Regex.Replace(source, @"\\[A-Za-z][^;\\]*;", "").Replace(@"\P", " ");
        return Regex.Replace(visible, @"[\s.{}]", "").Equals("图号DWGNO", StringComparison.OrdinalIgnoreCase);
    }

    public static BilingualFixedLabelSelection Select(
        IReadOnlyList<BilingualFixedLabelSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var mixed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (BilingualFixedLabelSample sample in samples)
        {
            if (TryKeepEmbeddedEnglish(sample.SourceText, out string existingEnglish))
            {
                mixed[sample.Id] = existingEnglish;
            }
        }

        BilingualFixedLabelSample[] english = samples
            .Where(sample => !ContainsCjk(sample.SourceText))
            .Where(sample => LatinTokens(sample.SourceText).Length > 0)
            .ToArray();
        BilingualFixedLabelSample[] chinese = samples
            .Where(sample => ContainsCjk(sample.SourceText))
            .Where(sample => !mixed.ContainsKey(sample.Id))
            .ToArray();
        PairCandidate[] directPairs = SelectOneToOnePairs(chinese, english, static pair =>
            IsPlausiblePair(pair.Chinese, pair.English) &&
            IsEquivalent(pair.Chinese.TranslatedText, pair.English.SourceText));
        var suppressed = directPairs
            .Select(pair => pair.Chinese.Id)
            .ToHashSet(StringComparer.Ordinal);
        var claimedEnglish = directPairs
            .Select(pair => pair.English.Id)
            .ToHashSet(StringComparer.Ordinal);

        PairCandidate[] learnedPairs = SelectOneToOnePairs(
            chinese.Where(sample => !suppressed.Contains(sample.Id)).ToArray(),
            english.Where(sample => !claimedEnglish.Contains(sample.Id)).ToArray(),
            pair => IsPlausiblePair(pair.Chinese, pair.English) &&
                    IsSupportedByKnownPair(pair, directPairs));
        foreach (PairCandidate pair in learnedPairs)
        {
            suppressed.Add(pair.Chinese.Id);
        }

        IReadOnlyDictionary<string, string> existingBySource = directPairs
            .Concat(learnedPairs)
            .ToDictionary(pair => pair.Chinese.Id, pair => pair.English.Id, StringComparer.Ordinal);

        return new BilingualFixedLabelSelection(
            suppressed.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            mixed,
            existingBySource);
    }

    private static bool TryKeepEmbeddedEnglish(string source, out string existingEnglish)
    {
        existingEnglish = string.Empty;
        if (!ContainsCjk(source) || !HasMeaningfulEnglish(source))
        {
            return false;
        }

        var result = new StringBuilder(source.Length);
        foreach (char value in source)
        {
            if (IsCjk(value) || value is >= '\u3000' and <= '\u303f')
            {
                continue;
            }
            result.Append(value);
        }

        existingEnglish = WhitespacePattern().Replace(result.ToString(), " ").Trim();
        return HasMeaningfulEnglish(existingEnglish);
    }

    private static PairCandidate[] SelectOneToOnePairs(
        IReadOnlyList<BilingualFixedLabelSample> chinese,
        IReadOnlyList<BilingualFixedLabelSample> english,
        Func<PairCandidate, bool> predicate)
    {
        var claimedChinese = new HashSet<string>(StringComparer.Ordinal);
        var claimedEnglish = new HashSet<string>(StringComparer.Ordinal);
        var selected = new List<PairCandidate>();
        IEnumerable<PairCandidate> candidates = chinese
            .SelectMany(left => english
                .Where(right => string.Equals(
                    right.DefinitionName,
                    left.DefinitionName,
                    StringComparison.Ordinal))
                .Select(right => new PairCandidate(left, right, PairDistance(left, right))))
            .Where(predicate)
            .OrderBy(pair => pair.Distance)
            .ThenBy(pair => pair.Chinese.Id, StringComparer.Ordinal)
            .ThenBy(pair => pair.English.Id, StringComparer.Ordinal);
        foreach (PairCandidate pair in candidates)
        {
            if (!claimedChinese.Add(pair.Chinese.Id) || !claimedEnglish.Add(pair.English.Id))
            {
                continue;
            }
            selected.Add(pair);
        }
        return selected.ToArray();
    }

    private static bool IsPlausiblePair(
        BilingualFixedLabelSample chinese,
        BilingualFixedLabelSample english)
    {
        Rect2 left = chinese.SourceBounds;
        Rect2 right = english.SourceBounds;
        double height = Math.Max(chinese.SourceHeight, english.SourceHeight);
        double minimumHeight = Math.Min(chinese.SourceHeight, english.SourceHeight);
        if (height <= 0 || minimumHeight <= 0 || height / minimumHeight > 1.60)
        {
            return false;
        }

        double verticalDistance = Math.Abs(left.Center.Y - right.Center.Y);
        double horizontalDistance = Math.Abs(left.Center.X - right.Center.X);
        bool sameRow = verticalDistance <= height * 0.40 &&
                       horizontalDistance <= height * 16;
        bool stacked = verticalDistance <= height * 3.25 &&
                       horizontalDistance <= Math.Max(height * 2, Math.Max(left.Width, right.Width) * 0.20);
        return sameRow || stacked;
    }

    private static bool IsSupportedByKnownPair(
        PairCandidate candidate,
        IReadOnlyList<PairCandidate> knownPairs)
    {
        double candidateDx = candidate.English.SourceBounds.Center.X - candidate.Chinese.SourceBounds.Center.X;
        double candidateDy = candidate.English.SourceBounds.Center.Y - candidate.Chinese.SourceBounds.Center.Y;
        double height = Math.Max(candidate.Chinese.SourceHeight, candidate.English.SourceHeight);
        return knownPairs.Any(known =>
        {
            if (!string.Equals(
                    known.Chinese.DefinitionName,
                    candidate.Chinese.DefinitionName,
                    StringComparison.Ordinal))
            {
                return false;
            }

            double knownDx = known.English.SourceBounds.Center.X - known.Chinese.SourceBounds.Center.X;
            double knownDy = known.English.SourceBounds.Center.Y - known.Chinese.SourceBounds.Center.Y;
            double horizontalTolerance = Math.Max(height * 2, Math.Abs(knownDx) * 0.20);
            double verticalTolerance = height * 0.75;
            return Math.Abs(candidateDx - knownDx) <= horizontalTolerance &&
                   Math.Abs(candidateDy - knownDy) <= verticalTolerance;
        });
    }

    private static double PairDistance(
        BilingualFixedLabelSample chinese,
        BilingualFixedLabelSample english)
    {
        double height = Math.Max(chinese.SourceHeight, english.SourceHeight);
        double dx = (english.SourceBounds.Center.X - chinese.SourceBounds.Center.X) / height;
        double dy = (english.SourceBounds.Center.Y - chinese.SourceBounds.Center.Y) / height;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool IsEquivalent(string translatedChinese, string existingEnglish)
    {
        string translatedLabel = CanonicalLabel(translatedChinese);
        string existingLabel = CanonicalLabel(existingEnglish);
        if (translatedLabel.Length == 0 || existingLabel.Length == 0)
        {
            return false;
        }

        if (string.Equals(translatedLabel, existingLabel, StringComparison.Ordinal))
        {
            return true;
        }

        string[] translated = translatedLabel.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] existing = existingLabel.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var left = translated.ToHashSet(StringComparer.Ordinal);
        var right = existing.ToHashSet(StringComparer.Ordinal);
        int intersection = left.Intersect(right, StringComparer.Ordinal).Count();
        int union = left.Union(right, StringComparer.Ordinal).Count();
        return intersection >= 2 && union > 0 && (double)intersection / union >= 0.60;
    }

    private static string[] EquivalentTokens(string value) =>
        LatinTokens(value)
            .Where(token => token != "by")
            .Select(token => token switch
            {
                "approved" => "approve",
                "checked" => "check",
                "designed" => "design",
                "prepared" => "prepare",
                "reviewed" => "review",
                _ => token
            })
            .ToArray();

    private static string CanonicalLabel(string value)
    {
        string label = string.Join(' ', EquivalentTokens(value));
        return label switch
        {
            "project name" => "project",
            "subitem code and name" => "item",
            "drawing title" => "title",
            "phase" => "stage",
            "drawing number" => "drawing no",
            _ => label
        };
    }

    private static string[] LatinTokens(string value) =>
        LatinWordPattern().Matches(value ?? string.Empty)
            .Select(match => match.Value.ToLowerInvariant())
            .Where(token => token.Length >= 2)
            .ToArray();

    private static bool HasMeaningfulEnglish(string value)
    {
        // Font names such as "MS PGothic" live inside MText control codes and
        // are not visible bilingual content. Classify only the visible text.
        string visible = MTextControlPattern().Replace(value ?? string.Empty, " ");
        // MDPX150 / ABCD-123 are equipment codes, not an English translation.
        // Do not strip Chinese from an already translated entity on that evidence.
        return MeaningfulEnglishWordPattern().IsMatch(visible);
    }

    private static bool ContainsCjk(string value) => value.Any(IsCjk);

    private static bool IsCjk(char value) => value is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff';

    [GeneratedRegex(@"[A-Za-z]+")]
    private static partial Regex LatinWordPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_.-])[A-Za-z]{4,}(?![A-Za-z0-9_.-])")]
    private static partial Regex MeaningfulEnglishWordPattern();

    [GeneratedRegex(@"\\[A-Za-z][^;]*;")]
    private static partial Regex MTextControlPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    private sealed record PairCandidate(
        BilingualFixedLabelSample Chinese,
        BilingualFixedLabelSample English,
        double Distance);
}

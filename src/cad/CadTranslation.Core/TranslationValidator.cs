using System.Text.RegularExpressions;
using CadTranslation.Contracts;

namespace CadTranslation.Core;

public static partial class TranslationValidator
{
    private static readonly Regex Marker = MarkerPattern();
    private static readonly Regex InvariantToken = InvariantTokenPattern();
    private static readonly (string Chinese, string Symbol)[] ChineseUnitSymbols =
    [
        ("平方毫米", "mm²"), ("平方厘米", "cm²"), ("平方米", "m²"),
        ("立方毫米", "mm³"), ("立方米", "m³"),
        ("摄氏度", "°C"), ("华氏度", "°F"),
        ("帕斯卡", "Pa"), ("兆帕", "MPa"), ("千帕", "kPa"),
        ("毫米", "mm"), ("厘米", "cm"), ("千米", "km"), ("微米", "µm"), ("纳米", "nm"),
        ("英寸", "in"), ("英尺", "ft"), ("毫升", "mL"),
        ("千克", "kg"), ("公斤", "kg"), ("牛顿", "N"), ("牛米", "N·m"),
        ("弧度", "rad"), ("分钟", "min"), ("小时", "h"), ("赫兹", "Hz"),
        ("千瓦", "kW"), ("伏特", "V"), ("安培", "A"), ("欧姆", "Ω"),
        ("吨", "t"), ("克", "g"), ("米", "m"), ("度", "°"), ("秒", "s"),
        ("升", "L"), ("帕", "Pa"), ("牛", "N"), ("瓦", "W"), ("伏", "V"),
        ("安", "A"), ("欧", "Ω")
    ];

    public static BatchValidationResult ValidateBatch(
        IReadOnlyList<ManifestRecord> manifestRecords,
        IReadOnlyList<TranslationRecord> translationRecords)
    {
        ThrowIfNull(manifestRecords);
        ThrowIfNull(translationRecords);

        var errors = new List<CommandError>();
        var manifestById = new Dictionary<string, ManifestRecord>(StringComparer.Ordinal);
        foreach (ManifestRecord? manifest in manifestRecords)
        {
            if (manifest is null)
            {
                errors.Add(Error("missing_manifest_record", "Manifest record is required.", null, null));
            }
            else if (string.IsNullOrWhiteSpace(manifest.RecordId))
            {
                errors.Add(Error("missing_record_id", "Manifest recordId is required.", manifest.RecordId, manifest.Handle));
            }
            else if (!manifestById.TryAdd(manifest.RecordId, manifest))
            {
                errors.Add(Error("duplicate_record_id", "Manifest recordId must be unique.", manifest.RecordId, manifest.Handle));
            }
        }

        var translationsById = new Dictionary<string, TranslationRecord>(StringComparer.Ordinal);
        foreach (TranslationRecord? translation in translationRecords)
        {
            if (translation is null)
            {
                errors.Add(Error("missing_translation_record", "Translation record is required.", null, null));
                continue;
            }

            if (string.IsNullOrWhiteSpace(translation.RecordId))
            {
                errors.Add(Error("missing_translation_record_id", "Translation recordId is required.", null, null));
                continue;
            }

            if (!manifestById.TryGetValue(translation.RecordId, out ManifestRecord? manifest))
            {
                errors.Add(Error("unknown_record_id", "Translation recordId is not in the manifest.", translation.RecordId, null));
                continue;
            }

            if (!translationsById.TryAdd(translation.RecordId, translation))
            {
                errors.Add(Error("duplicate_translation_record_id", "Translation recordId must be unique.", translation.RecordId, manifest.Handle));
                continue;
            }

            ValidateTranslation(manifest, translation, errors);
        }

        foreach (ManifestRecord manifest in manifestById.Values)
        {
            if (!translationsById.ContainsKey(manifest.RecordId))
            {
                errors.Add(Error("missing_translation", "Every manifest record requires a translation.", manifest.RecordId, manifest.Handle));
            }
        }

        return new BatchValidationResult(errors.Count == 0, errors);
    }

    private static void ValidateTranslation(ManifestRecord manifest, TranslationRecord translation, List<CommandError> errors)
    {
        if (manifest.ProtectedTokens is null || manifest.ProtectedTokens.Any(token =>
                token is null ||
                string.IsNullOrWhiteSpace(token.Marker) ||
                token.Kind is null ||
                token.Raw is null))
        {
            errors.Add(Error("malformed_protected_tokens", "Manifest protectedTokens must contain complete token objects.", manifest.RecordId, manifest.Handle));
            return;
        }

        if (!string.Equals(RestoreProtectedTokens(manifest.PlainText ?? string.Empty, manifest.ProtectedTokens), manifest.RawText ?? string.Empty, StringComparison.Ordinal))
        {
            errors.Add(Error("manifest_text_roundtrip_mismatch", "Manifest plainText and protectedTokens cannot reconstruct rawText exactly.", manifest.RecordId, manifest.Handle));
            return;
        }

        if (string.IsNullOrWhiteSpace(translation.InputHash))
        {
            errors.Add(Error("missing_input_hash", "Translation inputHash is required.", manifest.RecordId, manifest.Handle));
        }
        else if (!string.Equals(manifest.InputHash, translation.InputHash, StringComparison.Ordinal))
        {
            errors.Add(Error("input_hash_mismatch", "Translation inputHash does not match the manifest.", manifest.RecordId, manifest.Handle));
        }

        if (translation.TranslatedText is null)
        {
            errors.Add(Error("missing_translated_text", "Translated text is required.", manifest.RecordId, manifest.Handle));
        }
        else if (!string.IsNullOrEmpty(manifest.PlainText) && string.IsNullOrWhiteSpace(translation.TranslatedText))
        {
            errors.Add(Error("empty_translation", "Translated text is required for non-empty source text.", manifest.RecordId, manifest.Handle));
        }

        IReadOnlyList<ProtectedToken> protectedTokens = manifest.ProtectedTokens;
        string translatedText = translation.TranslatedText ?? string.Empty;
        string[] expectedMarkers = protectedTokens.Select(token => token.Marker).ToArray();
        string[] actualMarkers = Marker.Matches(translatedText).Cast<Match>().Select(match => match.Value).ToArray();
        if (!expectedMarkers.SequenceEqual(actualMarkers, StringComparer.Ordinal))
        {
            errors.Add(Error("protected_marker_mismatch", "Protected markers must be present exactly once and in order.", manifest.RecordId, manifest.Handle));
        }

        string restoredTranslation = RestoreProtectedTokensForOutput(translatedText, protectedTokens);
        string[] expectedInvariants = NormalizeInvariantTokens(manifest.RawText ?? string.Empty).ToArray();
        string[] actualInvariants = NormalizeInvariantTokens(restoredTranslation).ToArray();
        if (!InvariantTokensMatch(manifest.PlainText ?? string.Empty, expectedInvariants, actualInvariants))
        {
            errors.Add(Error("numeric_or_protected_token_mismatch", $"Numeric, unit, or model tokens changed. Expected [{string.Join(", ", expectedInvariants)}]; actual [{string.Join(", ", actualInvariants)}].", manifest.RecordId, manifest.Handle));
        }

        if (translation.ReviewStatus is null)
        {
            errors.Add(Error("missing_review_status", "reviewStatus is required.", manifest.RecordId, manifest.Handle));
        }
        else if (translation.ReviewStatus is not ("approved" or "manual-review"))
        {
            errors.Add(Error("invalid_review_status", "reviewStatus must be approved or manual-review.", manifest.RecordId, manifest.Handle));
        }
    }

    private static IEnumerable<string> NormalizeInvariantTokens(string value) =>
        Regex.Split(value, @"\\P", RegexOptions.IgnoreCase)
            .SelectMany(paragraph => InvariantToken.Matches(paragraph).Cast<Match>())
            .Select(match => NormalizeInvariantToken(match.Value));

    private static string NormalizeInvariantToken(string value)
    {
        string normalized = Regex.Replace(value, @"\s+", string.Empty);
        foreach ((string chinese, string symbol) in ChineseUnitSymbols)
        {
            if (normalized.EndsWith(chinese, StringComparison.Ordinal))
            {
                normalized = normalized[..^chinese.Length] + symbol;
                break;
            }
        }
        // Model identifiers retain their existing case-insensitive comparison;
        // physical unit prefixes must preserve case (milli versus mega).
        return normalized.Length > 0 && IsAsciiLetter(normalized[0])
            ? normalized.ToUpperInvariant()
            : normalized.Replace('μ', 'µ');
    }

    public static string RestoreProtectedTokens(string value, IReadOnlyList<ProtectedToken> tokens)
    {
        foreach (ProtectedToken token in tokens)
        {
            value = value.Replace(token.Marker, token.Raw, StringComparison.Ordinal);
        }

        return value;
    }

    public static string RestoreProtectedTokensForOutput(string value, IReadOnlyList<ProtectedToken> tokens)
    {
        foreach (ProtectedToken token in tokens)
        {
            ProtectedToken outputToken = NormalizeProtectedTokenForOutput(token);
            value = value.Replace(outputToken.Marker, outputToken.Raw, StringComparison.Ordinal);
        }

        return value;
    }

    public static ProtectedToken NormalizeProtectedTokenForOutput(ProtectedToken token)
    {
        ThrowIfNull(token);
        string outputRaw = token.Kind == "number-unit"
            ? NormalizeChineseUnitForOutput(token.Raw)
            : token.Raw.Replace(@"\F宋体|", @"\FSimSun|", StringComparison.OrdinalIgnoreCase);
        return token with { Raw = outputRaw };
    }

    private static string NormalizeChineseUnitForOutput(string value)
    {
        foreach ((string chinese, string symbol) in ChineseUnitSymbols)
        {
            if (value.EndsWith(chinese, StringComparison.Ordinal))
                return value[..^chinese.Length].TrimEnd() + symbol;
        }
        return value;
    }

    public static bool HasSameInvariantTokens(string source, string candidate) =>
        InvariantTokensMatch(
            source ?? string.Empty,
            NormalizeInvariantTokens(source ?? string.Empty).ToArray(),
            NormalizeInvariantTokens(candidate ?? string.Empty).ToArray());

    // Chinese counting characters never enter the invariant token set, yet their faithful rendering is
    // a digit (十六 -> 16, 七级 -> Grade 7, 第十一条 -> Article 11). Allow exactly the numbers whose
    // values parse from the source's numeral runs; any other added digit is still a hallucinated value.
    private static bool InvariantTokensMatch(string plainSource, string[] expected, string[] actual)
    {
        if (expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            return true;
        }

        List<string> remainder = new List<string>(actual);
        foreach (long value in ChineseNumeralValues(Marker.Replace(plainSource ?? string.Empty, string.Empty)))
        {
            remainder.Remove(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return remainder.SequenceEqual(expected, StringComparer.Ordinal);
    }

    private static IEnumerable<long> ChineseNumeralValues(string text)
    {
        string trimmed = (text ?? string.Empty);
        int index = 0;
        while (index < trimmed.Length)
        {
            if (ChineseNumeralValue(trimmed[index]) < 0)
            {
                index++;
                continue;
            }

            int end = index;
            while (end < trimmed.Length && ChineseNumeralValue(trimmed[end]) >= 0)
            {
                end++;
            }

            if (TryParseChineseNumeral(trimmed.Substring(index, end - index), out long value))
            {
                yield return value;
            }

            index = end;
        }
    }

    private static bool TryParseChineseNumeral(string text, out long value)
    {
        value = 0;
        string trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > 8)
        {
            return false;
        }

        long total = 0;
        long current = 0;
        bool any = false;
        foreach (char character in trimmed)
        {
            int digit = ChineseNumeralValue(character);
            if (digit < 0)
            {
                if (character is ' ' or '　' or '.' or '、' or ',' or '，' or '。')
                {
                    continue;
                }

                return false;
            }

            any = true;
            if (digit >= 10)
            {
                total += (current == 0 ? 1 : current) * digit;
                current = 0;
            }
            else
            {
                current = digit;
            }
        }

        if (!any)
        {
            return false;
        }

        value = total + current;
        return value > 0;
    }

    private static int ChineseNumeralValue(char character) => character switch
    {
        '零' or '〇' => 0,
        '一' or '壹' => 1,
        '二' or '两' or '贰' => 2,
        '三' or '叁' => 3,
        '四' or '肆' => 4,
        '五' or '伍' => 5,
        '六' or '陆' => 6,
        '七' or '柒' => 7,
        '八' or '捌' => 8,
        '九' or '玖' => 9,
        '十' or '拾' => 10,
        '百' or '佰' => 100,
        '千' or '仟' => 1000,
        '万' => 10000,
        _ => -1,
    };

    private static CommandError Error(string code, string message, string? recordId, string? handle) =>
        new(code, message, recordId, handle);

    private const string MarkerPatternText = "⟦P\\d{4}⟧";
    private const string InvariantTokenPatternText = @"Ø[+-]?(?:\d+[\.,]?\d*|[\.,]\d+)|" + ProtectedText.NumberPattern + "|" + ProtectedText.ModelPattern;

#if NET7_0_OR_GREATER
    [GeneratedRegex(MarkerPatternText)]
    private static partial Regex MarkerPattern();

    [GeneratedRegex(InvariantTokenPatternText)]
    private static partial Regex InvariantTokenPattern();
#else
    // .NET Framework hosts (AutoCAD 2020-2024) have no GeneratedRegex source generator.
    private static readonly Regex MarkerPatternValue = new Regex(MarkerPatternText, RegexOptions.Compiled);
    private static readonly Regex InvariantTokenPatternValue = new Regex(InvariantTokenPatternText, RegexOptions.Compiled);

    private static Regex MarkerPattern() => MarkerPatternValue;
    private static Regex InvariantTokenPattern() => InvariantTokenPatternValue;
#endif
}

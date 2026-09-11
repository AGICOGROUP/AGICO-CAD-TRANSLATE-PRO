using System.Text;
using System.Text.RegularExpressions;
using CadTranslation.Contracts;

namespace CadTranslation.Core;

public static partial class ProtectedText
{
    private const string ChineseUnit = @"(?:毫米|厘米|千米|微米|纳米|英寸|英尺|平方米|平方毫米|平方厘米|立方米|立方毫米|毫升|千克|公斤|吨|克|兆帕|千帕|帕斯卡|牛顿|牛米|弧度|摄氏度|华氏度|分钟|小时|秒|赫兹|瓦特|千瓦|伏特|安培|欧姆|米|度|升|帕|牛|瓦|伏|安|欧)";
    // Only recognized engineering units may attach to a number; prose is never a unit.
    private const string UnitSymbol = @"(?i:MPa|kPa|Pa|bar|psi|mm|cm|km|[μµ]m|nm|kg|mL|Hz|kWh|kW|kV|mA|rpm|tpd|rad|min|in|ft|°C|°F|[mgtNsLhWVAΩ°%])(?:[²³]|\^[23])?";
    internal const string NumberPattern = @"[+-]?(?:\d{1,3}(?:[ ,]\d{3})+|\d+)(?:[\.,]\d+)?(?:\s*(?:" + UnitSymbol + @"(?:[·*/-]" + UnitSymbol + @")*(?![A-Za-z])|" + ChineseUnit + @"))?";
    // No. is a drawing-number label, whereas A-20 and MDPX150 are model codes.
    internal const string ModelPattern = @"(?<![A-Za-z0-9])(?!(?i:No)\.)[A-Za-z]+(?:[-_./]?[A-Za-z0-9]+)*\d+(?:[-_./]?[A-Za-z0-9]+)*";
    private static readonly Regex Placeholder = PlaceholderPattern();
    private static readonly Regex DiameterNumber = DiameterNumberPattern();
    private static readonly Regex NumberWithUnit = NumberWithUnitPattern();
    private static readonly Regex ModelCode = ModelCodePattern();

    public static ParsedText Parse(string rawText)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        EnsureBalancedBraces(rawText);

        var plain = new StringBuilder(rawText.Length);
        var template = new StringBuilder(rawText.Length);
        var tokens = new List<ProtectedToken>();

        for (int index = 0; index < rawText.Length;)
        {
            if (TryRead(rawText, index, out string kind, out string raw))
            {
                AppendToken(plain, template, tokens, kind, raw);
                index += raw.Length;
                continue;
            }

            if (rawText[index] is '{' or '}')
            {
                AppendToken(plain, template, tokens, "mtext-brace", rawText[index].ToString());
                index++;
                continue;
            }

            char current = rawText[index++];
            plain.Append(current);
            template.Append(current);
        }

        return new ParsedText(plain.ToString(), template.ToString(), tokens);
    }

    private static void AppendToken(StringBuilder plain, StringBuilder template, ICollection<ProtectedToken> tokens, string kind, string raw)
    {
        string marker = $"\u27e6P{tokens.Count + 1:0000}\u27e7";
        tokens.Add(new ProtectedToken(marker, kind, raw));
        plain.Append(marker);
        template.Append(marker);
    }

    private static bool TryRead(string value, int index, out string kind, out string raw)
    {
        string remaining = value[index..];
        if (remaining.StartsWith("%<", StringComparison.Ordinal))
        {
            int fieldEnd = remaining.IndexOf(">%", StringComparison.Ordinal);
            if (fieldEnd >= 0)
            {
                kind = "field";
                raw = remaining[..(fieldEnd + 2)];
                return true;
            }
        }

        if (remaining.StartsWith("\\P", StringComparison.Ordinal))
        {
            kind = "mtext-code";
            raw = "\\P";
            return true;
        }

        if (remaining.StartsWith("\\X", StringComparison.Ordinal))
        {
            kind = "dimension-code";
            raw = "\\X";
            return true;
        }

        if (remaining.StartsWith("<>", StringComparison.Ordinal))
        {
            kind = "dimension-placeholder";
            raw = "<>";
            return true;
        }

        if (remaining.Length >= 3 && remaining[0] == '\\' && "HWCFTQAS".Contains(char.ToUpperInvariant(remaining[1])))
        {
            int terminator = remaining.IndexOf(';');
            if (terminator >= 0)
            {
                kind = "mtext-code";
                raw = remaining[..(terminator + 1)];
                return true;
            }
        }

        if (remaining.StartsWith("%%d", StringComparison.OrdinalIgnoreCase) ||
            remaining.StartsWith("%%p", StringComparison.OrdinalIgnoreCase) ||
            remaining.StartsWith("%%c", StringComparison.OrdinalIgnoreCase))
        {
            kind = "acad-symbol";
            raw = remaining[..3];
            return true;
        }

        Match match = Placeholder.Match(remaining);
        if (match.Success && match.Index == 0)
        {
            kind = "placeholder";
            raw = match.Value;
            return true;
        }

        match = DiameterNumber.Match(remaining);
        if (match.Success && match.Index == 0)
        {
            kind = "number-unit";
            raw = match.Value;
            return true;
        }

        match = NumberWithUnit.Match(remaining);
        if (match.Success && match.Index == 0)
        {
            kind = "number-unit";
            raw = match.Value;
            return true;
        }

        match = ModelCode.Match(remaining);
        if (match.Success && match.Index == 0 &&
            (index == 0 || !char.IsAsciiLetterOrDigit(value[index - 1])))
        {
            kind = "model-code";
            raw = match.Value;
            return true;
        }

        kind = string.Empty;
        raw = string.Empty;
        return false;
    }

    private static void EnsureBalancedBraces(string rawText)
    {
        int depth = 0;
        foreach (char character in rawText)
        {
            if (character == '{') depth++;
            else if (character == '}' && --depth < 0) throw new FormatException("MTEXT braces are unbalanced.");
        }

        if (depth != 0) throw new FormatException("MTEXT braces are unbalanced.");
    }

    [GeneratedRegex(@"^(?:\{[A-Za-z_][A-Za-z0-9_]*\}|\$\{[A-Za-z_][A-Za-z0-9_]*\}|%s|%\d+)")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"^Ø[+-]?(?:\d+[\.,]?\d*|[\.,]\d+)")]
    private static partial Regex DiameterNumberPattern();

    [GeneratedRegex("^" + NumberPattern)]
    private static partial Regex NumberWithUnitPattern();

    [GeneratedRegex("^" + ModelPattern)]
    private static partial Regex ModelCodePattern();
}

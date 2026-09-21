using System.Text.RegularExpressions;

namespace CadTranslation.Core;

// Exact text plus a bounded title-block vocabulary; never fuzzy-match equipment,
// quantities, model codes, or process descriptions.
public static class BilingualLabelEquivalence
{
    public static bool Matches(string left, string right)
    {
        string key = Key(left);
        return key.Length > 0 && key == Key(right);
    }

    // An existing translation may omit a decimal equipment tag already visible
    // in its Chinese label. Retain exact wording and reject any other numbers.
    public static bool MatchesNeighbor(string source, string target, string existing)
    {
        if (Matches(target, existing)) return true;
        var tag = Regex.Match(source, @"^\s*(\d+\.\d+(?:-\d+)?)\s*(?=[\u3400-\u9fff])");
        if (!tag.Success || Regex.IsMatch(existing, @"\d")) return false;
        var prefix = Regex.Match(target, @"^\s*" + Regex.Escape(tag.Groups[1].Value) + @"\s+");
        return prefix.Success && Matches(target[prefix.Length..], existing);
    }

    // Presence of Latin letters is only a candidate signal. Reuse the complete
    // visible counterpart, including its qualifiers and engineering parameters.
    public static bool MatchesInline(string source, string target)
    {
        if (Matches(source, target)) return true;
        if (!Regex.IsMatch(source, @"[\u3400-\u9fff\uf900-\ufaff]") ||
            Regex.IsMatch(target, @"[\u3400-\u9fff\uf900-\ufaff]")) return false;
        string existing = Regex.Replace(source, @"[\u3400-\u9fff\uf900-\ufaff]+", " ");
        if (Matches(existing, target)) return true;
        // A bilingual entity may repeat the same equipment tag on both lines.
        var tag = Regex.Match(source, @"^\s*(\d+(?:\.\d+)*(?:-\d+)?)\s*(?=[\u3400-\u9fff])");
        if (!tag.Success) return false;
        var prefix = Regex.Match(existing, @"^\s*" + Regex.Escape(tag.Groups[1].Value) + @"\s+");
        return prefix.Success && Matches(existing[prefix.Length..], target) &&
            Regex.IsMatch(target, @"^\s*" + Regex.Escape(tag.Groups[1].Value) + @"\s+");
    }

    private static string Key(string text)
    {
        string key = Regex.Replace(text.ToUpperInvariant().Trim(), @"\s+", " ");
        // Compact only known title labels. Decimal points, signs, model-code
        // separators and word boundaries must survive ordinary comparisons.
        string title = Regex.IsMatch(key, @"^[A-Z .]+$") ? Regex.Replace(key, @"[ .]+", "") : key;
        return title switch
        {
            "DESIGN" or "DESIGNEDBY" => "DESIGN",
            "CHECK" or "CHECKEDBY" or "PROOFREAD" => "CHECK",
            "REVIEWEDBY" or "REVIEW" or "EXAMINE" => "REVIEWED",
            "APPROVEDBY" or "APPROVE" or "EXAMINEANDAPPROVE" => "APPROVED",
            "PROJECTLEAD" or "PROJECTMANAGER" => "PROJECTLEAD",
            "DISCIPLINELEAD" or "PROFESSIONALLEADER" => "DISCIPLINELEAD",
            "DISCIPLINE" or "PROFESSION" => "DISCIPLINE",
            "DRAWINGTITLE" or "DWGTITLE" => "DRAWINGTITLE",
            "DRAWINGNUMBER" or "DWGNO" or "DRAWINGNO" => "DRAWINGNUMBER",
            "PROJECTNUMBER" or "PROJECTNO" => "PROJECTNUMBER",
            "SUBITEM" or "SUBPROJECT" => "SUBITEM",
            _ => key
        };
    }
}

using System.Text.RegularExpressions;

namespace CadTranslation.Core;

// Exact text plus a bounded title-block vocabulary; never fuzzy-match equipment,
// quantities, model codes, or process descriptions.
public static class BilingualLabelEquivalence
{
    public static bool Matches(string left, string right) => Key(left) == Key(right) && Key(left).Length > 0;

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

    private static string Key(string text)
    {
        string key = Regex.Replace(text.ToUpperInvariant(), @"[\s\p{P}]+", "");
        return key switch
        {
            "DESIGNEDBY" => "DESIGN",
            "CHECKEDBY" or "PROOFREAD" => "CHECK",
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

using System.Text.RegularExpressions;

namespace CadTranslation.Core;

// Exact text plus a bounded title-block vocabulary; never fuzzy-match equipment,
// quantities, model codes, or process descriptions.
public static class BilingualLabelEquivalence
{
    public static bool Matches(string left, string right) => Key(left) == Key(right) && Key(left).Length > 0;

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

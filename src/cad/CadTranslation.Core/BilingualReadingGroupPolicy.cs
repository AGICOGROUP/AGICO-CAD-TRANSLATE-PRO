using System.Text.RegularExpressions;

namespace CadTranslation.Core;

/// <summary>Aligned short labels are lists, not paragraphs that may be relocated.</summary>
public static class BilingualReadingGroupPolicy
{
    public static bool IsNarrative(IEnumerable<string> texts)
    {
        var rows=texts.Select(t=>Regex.Replace(t,@"\s+"," ").Trim()).Where(t=>t.Length>0).ToArray();
        bool Numbered(string t)=>Regex.IsMatch(t,@"^[(（]?\d+(?:\.\d+)*[.．、)）]\s*\p{L}") && t.Length>=10;
        bool Definition(string t)=>Regex.IsMatch(t,@"^[A-Za-z]\d+\s*[:：]") && t.Length>=10;
        bool Prose(string t)=>t.Count(c=>c is >= '\u3400' and <= '\u9fff')>=30 ||
            t.Length>=100 && t.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length>=12;
        return rows.Count(t=>Numbered(t)||Definition(t)||Prose(t))>=2 || rows.Any(t=>t.Length>=160);
    }
}

using System.Text.RegularExpressions;

namespace CadTranslation.Core;

// Exact visible field names: equipment descriptions containing these words are not title panels.
public static class BilingualTitlePanel
{
    public static bool IsTitlePanel(IEnumerable<string> labels)
    {
        var fields = labels.Select(label => Regex.Replace(label, @"[^\u3400-\u9fff]", ""))
            .Where(label => label is "审定" or "设总" or "审核" or "校核" or "校对" or "设计" or "制图"
                or "批准" or "会签" or "项目经理" or "图号" or "图名" or "设计阶段" or "工程名称" or "项目名称")
            .Distinct().Take(2).Count();
        return fields == 2;
    }
}

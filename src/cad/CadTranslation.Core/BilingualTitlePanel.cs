using System.Text.RegularExpressions;

namespace CadTranslation.Core;

// Exact visible field names: equipment descriptions containing these words are not title panels.
public static class BilingualTitlePanel
{
    public static bool IsTitleField(string label) => Normalize(label) is "审定" or "设总" or "审核" or "审校" or "校核" or "校对" or "设计" or "制图"
        or "批准" or "会签" or "项目经理" or "图号" or "图名" or "设计阶段" or "工程名称" or "项目名称"
        or "日期" or "签名" or "实名" or "专业" or "比例" or "页数" or "图纸名称" or "子项名称";
    private static string Normalize(string label) => Regex.Replace(label, @"[^\u3400-\u9fff]", "");
    public static bool IsTitlePanel(IEnumerable<string> labels)
    {
        var names = labels.Select(Normalize).Distinct().ToArray();
        int fields = names.Count(IsTitleField);
        return fields >= 2 || fields == 1 && names.Any(name => name is "工程" or "阶段" or "名称");
    }
}

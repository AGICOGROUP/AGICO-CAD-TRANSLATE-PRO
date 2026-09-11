namespace CadTranslation.Core;

public static class NarrativeTargetFontPolicy
{
    private const string LatinFontPrefix = @"{\FArial;";

    public static string ApplyLatinWrapper(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Count(char.IsLetter) < 4 ||
            value.Any(IsCjk))
        {
            return value;
        }

        return LatinFontPrefix + value + "}";
    }

    internal static string RemoveLatinWrapper(string value)
    {
        if (!value.StartsWith(LatinFontPrefix, StringComparison.Ordinal) ||
            !value.EndsWith('}'))
        {
            return value;
        }

        return value.Substring(LatinFontPrefix.Length, value.Length - LatinFontPrefix.Length - 1);
    }

    private static bool IsCjk(char value) => value is
        >= '\u3400' and <= '\u4dbf' or
        >= '\u4e00' and <= '\u9fff' or
        >= '\uf900' and <= '\ufaff';
}

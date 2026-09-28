namespace CadTranslation.Core;

public static class NarrativeTargetFontPolicy
{
    private const string LatinFontPrefix = @"{\FArial;";

    // The conventional face used whenever the source face cannot render the target.
    internal const string ConventionalLatinFontCode = @"\FArial;";

    // Single-byte-complemented CJK faces carry no latin glyphs, so applying one to latin
    // target text renders blank boxes or full-width glyphs. They are never reused.
    private static readonly string[] CjkOnlyFaces =
    {
        "hztxt", "hzst", "hzkt", "hzfs", "hzdx", "hzpop", "hztxtb", "gbcbig", "gbcbigs",
        "chineset", "bigfont",
    };

    // Reuse the source's own face when it can render the target script; otherwise fall back
    // to the most conventional face, so an addition can never fail to render.
    public static string ResolvePrefix(string? sourceFontSpec)
    {
        string? reusable = ReusableFace(sourceFontSpec);
        return reusable is null ? ConventionalLatinFontCode : @"\F" + reusable + ";";
    }

    private static string? ReusableFace(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return null;
        }

        // In an MText font code the first name is the face and any later name is its big
        // font, so only the first entry decides whether latin text can render at all.
        // IsNullOrWhiteSpace carries no NotNullWhen annotation on .NET Framework, hence the forgiveness.
        string face = spec!.Split('|')![0].Split(',')![0].Trim();
        if (face.Length == 0 || face.Any(IsCjk))
        {
            return null;
        }

        return CjkOnlyFaces.Contains(face.Split('.')[0].ToLowerInvariant()) ? null : spec.Trim();
    }

    public static string ApplyLatinWrapper(string value) => ApplyLatinWrapper(value, null);

    public static string ApplyLatinWrapper(string value, string? sourceFontSpec)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Count(char.IsLetter) < 4 ||
            value.Any(IsCjk))
        {
            return value;
        }

        return "{" + ResolvePrefix(sourceFontSpec) + value + "}";
    }

    internal static string RemoveLatinWrapper(string value)
    {
        // Strip the wrapper this policy generates, whether its face came from the source or
        // from the conventional fallback, so verifiers compare content rather than face.
        if (value.StartsWith(LatinFontPrefix, StringComparison.Ordinal) && value.EndsWith('}'))
        {
            return value.Substring(LatinFontPrefix.Length, value.Length - LatinFontPrefix.Length - 1);
        }

        int close = value.IndexOf(";", StringComparison.Ordinal);
        if (value.StartsWith(@"{\F", StringComparison.Ordinal) && close > 0 && value.EndsWith('}'))
        {
            return value.Substring(close + 1, value.Length - close - 2);
        }

        return value;
    }

    private static bool IsCjk(char value) => value is
        >= '\u3400' and <= '\u4dbf' or
        >= '\u4e00' and <= '\u9fff' or
        >= '\uf900' and <= '\ufaff';
}

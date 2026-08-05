namespace CadTranslation.Core;

public static class NarrativeCandidatePolicy
{
    public static bool CanUseAuthoritativeSelector(string regionId) =>
        regionId.StartsWith("note-column-", StringComparison.Ordinal) ||
        regionId.StartsWith("frame-", StringComparison.Ordinal);

    public static bool CanFeedGenericDetector(string regionId, bool alreadyClaimed)
    {
        if (alreadyClaimed)
        {
            return false;
        }

        return string.IsNullOrEmpty(regionId) ||
            regionId.StartsWith("note-column-", StringComparison.Ordinal);
    }
}

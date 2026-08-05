namespace CadTranslation.Core;

public static class LayoutCoveragePolicy
{
    public static string[] FindUncovered(
        IReadOnlyList<string> changedRecordIds,
        IReadOnlyList<string> handledRecordIds)
    {
        var handled = new HashSet<string>(handledRecordIds, StringComparer.Ordinal);
        return changedRecordIds
            .Where(recordId => !handled.Contains(recordId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(recordId => recordId, StringComparer.Ordinal)
            .ToArray();
    }
}

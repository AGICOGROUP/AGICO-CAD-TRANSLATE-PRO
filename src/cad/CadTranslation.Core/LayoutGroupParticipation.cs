namespace CadTranslation.Core;

public sealed record LayoutParticipationItem(
    string RecordId,
    string RegionId,
    bool IsChanged);

public static class LayoutGroupParticipation
{
    public static string[] SelectNarrativeParticipants(
        IReadOnlyList<LayoutParticipationItem> items)
    {
        var changedRegions = new HashSet<string>(
            items.Where(item => item.IsChanged)
                .Select(item => item.RegionId)
                .Where(regionId => !string.IsNullOrWhiteSpace(regionId)),
            StringComparer.Ordinal);
        return items
            .Where(item => changedRegions.Contains(item.RegionId))
            .Select(item => item.RecordId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}

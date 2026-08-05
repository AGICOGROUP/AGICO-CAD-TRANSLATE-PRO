namespace CadTranslation.Core;

public sealed record LayoutOverlapItem(
    string RecordId,
    string DefinitionName,
    string RegionId);

public sealed record LayoutOverlapPair(
    string LeftRecordId,
    string RightRecordId);

public static class LayoutOverlapPairSelector
{
    public static LayoutOverlapPair[] Select(
        IReadOnlyList<LayoutOverlapItem> items)
    {
        var result = new List<LayoutOverlapPair>();
        foreach (IGrouping<string, LayoutOverlapItem> group in items.GroupBy(
                     item => item.DefinitionName,
                     StringComparer.Ordinal))
        {
            LayoutOverlapItem[] definitionItems = group.ToArray();
            for (int leftIndex = 0; leftIndex < definitionItems.Length; leftIndex++)
            {
                for (int rightIndex = leftIndex + 1; rightIndex < definitionItems.Length; rightIndex++)
                {
                    result.Add(new LayoutOverlapPair(
                        definitionItems[leftIndex].RecordId,
                        definitionItems[rightIndex].RecordId));
                }
            }
        }

        return result.ToArray();
    }
}

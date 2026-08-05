namespace CadTranslation.Core;

public static class LogicalTextSegmenter
{
    public static LogicalComposedRow[][] SplitAtSourceGaps(
        IReadOnlyList<LogicalComposedRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var segments = new List<List<LogicalComposedRow>>();
        foreach (LogicalComposedRow row in rows)
        {
            if (segments.Count == 0 || (row.ParagraphGapBefore && segments[^1].Count > 0))
            {
                segments.Add([]);
            }
            segments[^1].Add(row);
        }
        return segments.Select(segment => segment.ToArray()).ToArray();
    }
}

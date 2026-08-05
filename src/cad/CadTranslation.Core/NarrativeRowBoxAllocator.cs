namespace CadTranslation.Core;

public static class NarrativeRowBoxAllocator
{
    public static IReadOnlyDictionary<string, Rect2> Allocate(
        Rect2 parent,
        IReadOnlyList<NarrativeRowItem> items,
        double gutter,
        double tolerance = 1e-6)
    {
        if (items.Count == 0)
        {
            return new Dictionary<string, Rect2>(StringComparer.Ordinal);
        }

        NarrativeRowItem[] ordered = items
            .OrderBy(item => item.SourceBounds.Center.X)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        double halfGutter = Math.Max(0, gutter) / 2;
        var sourceBoxes = new Dictionary<string, Rect2>(StringComparer.Ordinal);
        bool valid = true;
        for (int index = 0; index < ordered.Length; index++)
        {
            double left = index == 0
                ? parent.Left
                : (ordered[index - 1].SourceBounds.Center.X +
                   ordered[index].SourceBounds.Center.X) / 2 + halfGutter;
            double right = index + 1 == ordered.Length
                ? parent.Right
                : (ordered[index].SourceBounds.Center.X +
                   ordered[index + 1].SourceBounds.Center.X) / 2 - halfGutter;
            left = Math.Max(parent.Left, left);
            right = Math.Min(parent.Right, right);
            if (right - left <= tolerance)
            {
                valid = false;
                break;
            }

            sourceBoxes[ordered[index].Id] =
                new Rect2(left, parent.Bottom, right, parent.Top);
        }

        if (valid)
        {
            return sourceBoxes;
        }

        double slotWidth = parent.Width / ordered.Length;
        if (slotWidth - Math.Max(0, gutter) <= tolerance)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parent),
                "Narrative row has insufficient width for positive text boxes.");
        }

        var fallback = new Dictionary<string, Rect2>(StringComparer.Ordinal);
        for (int index = 0; index < ordered.Length; index++)
        {
            double left = parent.Left + slotWidth * index +
                          (index == 0 ? 0 : halfGutter);
            double right = parent.Left + slotWidth * (index + 1) -
                           (index + 1 == ordered.Length ? 0 : halfGutter);
            fallback[ordered[index].Id] =
                new Rect2(left, parent.Bottom, right, parent.Top);
        }

        return fallback;
    }
}

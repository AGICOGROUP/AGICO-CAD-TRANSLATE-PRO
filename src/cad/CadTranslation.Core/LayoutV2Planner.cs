namespace CadTranslation.Core;

public enum LayoutV2Kind
{
    TableCell,
    Narrative,
    FixedLabel
}

public sealed record LayoutV2Input(
    string RecordId,
    string GroupId,
    LayoutV2Kind Kind,
    Rect2 SourceBounds,
    Rect2 ParentBounds,
    double OriginalTextHeight,
    IReadOnlyList<Rect2> HardKeepouts);

public sealed record LayoutV2Decision(
    string RecordId,
    LayoutV2Kind Kind,
    Rect2 AllowedBounds,
    bool ForceWrap,
    bool ManualReview,
    string Reason);

public static class LayoutV2Planner
{
    public static LayoutV2Decision[] Plan(
        IReadOnlyList<LayoutV2Input> inputs,
        double tolerance = 1e-6)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        Validate(inputs);

        var allowedById = new Dictionary<string, Rect2>(StringComparer.Ordinal);
        foreach (IGrouping<(string GroupId, LayoutV2Kind Kind), LayoutV2Input> group in
                 inputs.GroupBy(input => (input.GroupId, input.Kind)))
        {
            LayoutV2Input[] members = group.ToArray();
            if (group.Key.Kind == LayoutV2Kind.Narrative)
            {
                Rect2 sourceEnvelope = Envelope(members.Select(input => input.SourceBounds));
                Rect2 parent = members.Aggregate(
                    members[0].ParentBounds,
                    (current, input) => Intersect(current, input.ParentBounds));
                LayoutV2Input representative = members[0] with
                {
                    SourceBounds = sourceEnvelope,
                    ParentBounds = parent,
                    HardKeepouts = members.SelectMany(input => input.HardKeepouts).Distinct().ToArray()
                };
                Rect2 shared = ClampNarrative(parent, representative, tolerance);
                foreach (LayoutV2Input input in members)
                {
                    allowedById.Add(input.RecordId, shared);
                }
                continue;
            }

            IReadOnlyDictionary<string, Rect2> slots = AllocatePeerSlots(members, tolerance);
            foreach (LayoutV2Input input in members)
            {
                Rect2 allowed = IntersectOrSource(
                    slots.GetValueOrDefault(input.RecordId, input.ParentBounds),
                    input.ParentBounds,
                    input.SourceBounds,
                    tolerance);
                if (input.Kind == LayoutV2Kind.Narrative)
                {
                    allowed = ClampNarrative(allowed, input, tolerance);
                }

                allowedById.Add(input.RecordId, allowed);
            }
        }

        return inputs.Select(input =>
        {
            Rect2 allowed = allowedById[input.RecordId];
            bool valid = allowed.Width > tolerance &&
                         allowed.Height > tolerance &&
                         (input.Kind == LayoutV2Kind.Narrative
                             ? allowed.Left <= input.SourceBounds.Left + tolerance &&
                               allowed.Right >= input.SourceBounds.Left + input.OriginalTextHeight - tolerance
                             : allowed.Contains(input.SourceBounds, tolerance));
            return new LayoutV2Decision(
                input.RecordId,
                input.Kind,
                allowed,
                input.Kind is LayoutV2Kind.TableCell or LayoutV2Kind.Narrative,
                !valid,
                valid ? "source-derived-region" : "source-region-conflict");
        }).ToArray();
    }

    private static IReadOnlyDictionary<string, Rect2> AllocatePeerSlots(
        IReadOnlyList<LayoutV2Input> inputs,
        double tolerance)
    {
        if (inputs.Count == 1 || inputs[0].Kind == LayoutV2Kind.Narrative)
        {
            return inputs.ToDictionary(
                input => input.RecordId,
                input => input.ParentBounds,
                StringComparer.Ordinal);
        }

        Rect2 parent = inputs.Aggregate(
            inputs[0].ParentBounds,
            (current, input) => Intersect(current, input.ParentBounds));
        LayoutTextBoxSample[] samples = inputs.Select(input => new LayoutTextBoxSample(
            input.RecordId,
            input.SourceBounds,
            input.OriginalTextHeight)).ToArray();

        return ExclusiveTextBoxAllocator.Allocate(parent, samples, tolerance);
    }

    private static Rect2 ClampNarrative(
        Rect2 allowed,
        LayoutV2Input input,
        double tolerance)
    {
        double gutter = Math.Max(input.OriginalTextHeight * 2, tolerance);
        double left = input.SourceBounds.Left;
        double right = allowed.Right;
        foreach (Rect2 keepout in input.HardKeepouts)
        {
            if (!VerticallyRelevant(input.SourceBounds, keepout, tolerance) ||
                keepout.Left <= input.SourceBounds.Left + gutter)
            {
                continue;
            }

            right = Math.Min(right, keepout.Left - gutter);
        }

        right = Math.Max(right, input.SourceBounds.Left + input.OriginalTextHeight);
        return new Rect2(left, allowed.Bottom, right, allowed.Top);
    }

    private static bool VerticallyRelevant(Rect2 source, Rect2 keepout, double tolerance) =>
        Math.Min(source.Top, keepout.Top) - Math.Max(source.Bottom, keepout.Bottom) > tolerance;

    private static Rect2 IntersectOrSource(
        Rect2 first,
        Rect2 second,
        Rect2 source,
        double tolerance)
    {
        Rect2 intersection = Intersect(first, second);
        return intersection.Width > tolerance &&
               intersection.Height > tolerance &&
               intersection.Contains(source, tolerance)
            ? intersection
            : source;
    }

    private static Rect2 Intersect(Rect2 first, Rect2 second) => new(
        Math.Max(first.Left, second.Left),
        Math.Max(first.Bottom, second.Bottom),
        Math.Min(first.Right, second.Right),
        Math.Min(first.Top, second.Top));

    private static Rect2 Envelope(IEnumerable<Rect2> values)
    {
        Rect2[] items = values.ToArray();
        return new Rect2(
            items.Min(item => item.Left),
            items.Min(item => item.Bottom),
            items.Max(item => item.Right),
            items.Max(item => item.Top));
    }

    private static void Validate(IReadOnlyList<LayoutV2Input> inputs)
    {
        if (inputs.Any(input =>
                string.IsNullOrWhiteSpace(input.RecordId) ||
                string.IsNullOrWhiteSpace(input.GroupId) ||
                input.OriginalTextHeight <= 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(inputs),
                "Layout v2 inputs require IDs and a positive source text height.");
        }

        string? duplicate = inputs.GroupBy(input => input.RecordId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Duplicate layout v2 record ID '{duplicate}'.",
                nameof(inputs));
        }
    }
}

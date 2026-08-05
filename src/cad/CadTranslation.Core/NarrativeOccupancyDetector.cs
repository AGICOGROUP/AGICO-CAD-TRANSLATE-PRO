using System.Text;

namespace CadTranslation.Core;

public sealed record NarrativeOccupancySample(
    string Id,
    Point2 Anchor,
    Rect2 Bounds,
    string Text,
    bool IsLeftAligned);

public sealed record NarrativeOccupancyGroup(
    LayoutRegion Region,
    IReadOnlyList<string> MemberIds);

public static class NarrativeClassificationTextPolicy
{
    public static string Select(string sourceText, string candidateText)
    {
        _ = candidateText;
        return sourceText;
    }
}

public static class NarrativeOccupancyDetector
{
    private sealed record Group(
        double StartX,
        Rect2 Bounds,
        IReadOnlyList<NarrativeOccupancySample> Samples);

    public static LayoutRegion[] Detect(
        IReadOnlyList<NarrativeLayoutSample> samples,
        double medianTextHeight,
        double tolerance = 1e-6) =>
        DetectGroups(
                samples.Select((sample, index) => new NarrativeOccupancySample(
                    index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    sample.Anchor,
                    sample.Bounds,
                    sample.Text,
                    sample.IsLeftAligned)).ToArray(),
                medianTextHeight,
                tolerance)
            .Select(group => group.Region)
            .ToArray();

    public static NarrativeOccupancyGroup[] DetectGroups(
        IReadOnlyList<NarrativeOccupancySample> samples,
        double medianTextHeight,
        double tolerance = 1e-6)
    {
        double height = Math.Max(medianTextHeight, tolerance);
        NarrativeOccupancySample[] candidates = samples
            .Where(sample => sample.IsLeftAligned)
            .OrderBy(sample => sample.Anchor.X)
            .ThenByDescending(sample => sample.Bounds.Center.Y)
            .ToArray();
        var groups = new List<Group>();

        foreach (IReadOnlyList<NarrativeOccupancySample> startBand in
                 BuildStartBands(candidates, height, tolerance))
        {
            foreach (IReadOnlyList<NarrativeOccupancySample> verticalRun in
                     SplitVerticalRuns(startBand, height, tolerance))
            {
                NarrativeOccupancySample[] component = verticalRun.ToArray();
                if (component.Length < 3 ||
                    component.Count(sample => VisibleTextLength(sample.Text) >= 16) < 2 ||
                    component.Sum(sample => VisibleTextLength(sample.Text)) < 60)
                {
                    continue;
                }

                groups.Add(new Group(
                    component.Average(sample => sample.Anchor.X),
                    Union(component.Select(sample => sample.Bounds)),
                    component));
            }
        }

        groups = IncludeInteriorContinuationRows(groups, samples, tolerance);
        groups = MergeDenseMacroColumns(groups, candidates, height, tolerance);
        Group[] ordered = groups
            .OrderBy(group => group.StartX)
            .ThenByDescending(group => group.Bounds.Top)
            .ToArray();
        Rect2[] exclusive = ordered.Select(group => group.Bounds).ToArray();
        double gutter = height * 0.10;
        for (int leftIndex = 0; leftIndex < ordered.Length; leftIndex++)
        {
            for (int rightIndex = leftIndex + 1; rightIndex < ordered.Length; rightIndex++)
            {
                if (!VerticallyOverlaps(ordered[leftIndex].Bounds, ordered[rightIndex].Bounds, tolerance) ||
                    ordered[rightIndex].StartX - ordered[leftIndex].StartX <= height * 2)
                {
                    continue;
                }

                double boundary = (ordered[leftIndex].StartX + ordered[rightIndex].StartX) / 2;
                Rect2 left = exclusive[leftIndex];
                Rect2 right = exclusive[rightIndex];
                if (left.Right >= right.Left - tolerance)
                {
                    exclusive[leftIndex] = new Rect2(
                        left.Left,
                        left.Bottom,
                        Math.Max(left.Left + height, boundary - gutter),
                        left.Top);
                    exclusive[rightIndex] = new Rect2(
                        Math.Min(right.Right - height, boundary + gutter),
                        right.Bottom,
                        right.Right,
                        right.Top);
                }
            }
        }

        return exclusive
            .Select((bounds, index) => new NarrativeOccupancyGroup(
                new LayoutRegion(
                    $"note-occupancy-{index + 1}",
                    LayoutRegionKind.NoteColumn,
                    bounds),
                ordered[index].Samples.Select(sample => sample.Id).ToArray()))
            .ToArray();
    }

    private static List<Group> IncludeInteriorContinuationRows(
        IReadOnlyList<Group> groups,
        IReadOnlyList<NarrativeOccupancySample> samples,
        double tolerance)
    {
        var additions = groups
            .Select(_ => new List<NarrativeOccupancySample>())
            .ToArray();
        var existingIds = new HashSet<string>(
            groups.SelectMany(group => group.Samples).Select(sample => sample.Id),
            StringComparer.Ordinal);

        foreach (NarrativeOccupancySample sample in samples.Where(sample =>
                     !existingIds.Contains(sample.Id) &&
                     (VisibleTextLength(sample.Text) >= 16 ||
                      IsShortNumberedHeading(sample) ||
                      IsShortCenteredInteriorFragment(sample))))
        {
            var matches = groups
                .Select((group, index) => new
                {
                    Index = index,
                    Overlap = HorizontalIntersection(group.Bounds, sample.Bounds),
                    Group = group
                })
                .Where(match =>
                    sample.Bounds.Center.Y >= match.Group.Bounds.Bottom - tolerance &&
                    sample.Bounds.Center.Y <= match.Group.Bounds.Top + tolerance &&
                    match.Overlap >=
                    Math.Min(match.Group.Bounds.Width, sample.Bounds.Width) * 0.50 - tolerance)
                .OrderByDescending(match => match.Overlap)
                .ThenBy(match => match.Group.Bounds.Area)
                .ToArray();
            if (matches.Length > 0)
            {
                additions[matches[0].Index].Add(sample);
            }
        }

        return groups
            .Select((group, index) =>
            {
                NarrativeOccupancySample[] members =
                    [.. group.Samples, .. additions[index]];
                return group with
                {
                    Bounds = Union(members.Select(sample => sample.Bounds)),
                    Samples = members
                };
            })
            .ToList();
    }

    private static double HorizontalIntersection(Rect2 left, Rect2 right) =>
        Math.Max(0, Math.Min(left.Right, right.Right) - Math.Max(left.Left, right.Left));

    private static bool IsShortNumberedHeading(NarrativeOccupancySample sample)
    {
        if (sample.IsLeftAligned)
        {
            return false;
        }

        string text = VisibleText(sample.Text).Trim();
        if (text.Length is < 3 or > 32 || !char.IsDigit(text[0]))
        {
            return false;
        }

        int separator = text.IndexOfAny(['.', '．']);
        return separator is > 0 and <= 4 &&
               text[(separator + 1)..].Any(character =>
                   char.IsLetter(character) ||
                   character is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff');
    }

    private static bool IsShortCenteredInteriorFragment(NarrativeOccupancySample sample) =>
        !sample.IsLeftAligned &&
        VisibleText(sample.Text).Count(char.IsDigit) >= 1 &&
        VisibleText(sample.Text).Count(char.IsLetter) <= 2;

    private static List<Group> MergeDenseMacroColumns(
        IReadOnlyList<Group> groups,
        IReadOnlyList<NarrativeOccupancySample> candidates,
        double height,
        double tolerance)
    {
        IReadOnlyList<IReadOnlyList<Group>> panels = PartitionVerticalPanels(
            groups,
            height,
            tolerance);
        if (panels.Count <= 1)
        {
            return MergeDenseMacroPanel(groups, candidates, height, tolerance);
        }

        var candidatesByPanel = panels
            .Select(_ => new List<NarrativeOccupancySample>())
            .ToArray();
        Rect2[] panelBounds = panels
            .Select(panel => Union(panel.Select(group => group.Bounds)))
            .ToArray();
        foreach (NarrativeOccupancySample candidate in candidates)
        {
            int nearest = Enumerable.Range(0, panelBounds.Length)
                .OrderBy(index => VerticalDistance(candidate.Anchor.Y, panelBounds[index]))
                .ThenBy(index => index)
                .First();
            if (VerticalDistance(candidate.Anchor.Y, panelBounds[nearest]) <=
                height * 3.50 + tolerance)
            {
                candidatesByPanel[nearest].Add(candidate);
            }
        }

        return panels
            .SelectMany((panel, index) => MergeDenseMacroPanel(
                panel,
                candidatesByPanel[index],
                height,
                tolerance))
            .ToList();
    }

    private static List<Group> MergeDenseMacroPanel(
        IReadOnlyList<Group> groups,
        IReadOnlyList<NarrativeOccupancySample> candidates,
        double height,
        double tolerance)
    {
        if (groups.Count < 6)
        {
            return groups.ToList();
        }

        double maximumWeight = groups.Max(group => group.Samples.Count);
        double minimumWeight = Math.Max(3, maximumWeight * 0.30);
        double minimumSeedDistance = height * 12;
        double[] starts = groups
            .Where(group => group.Samples.Count >= minimumWeight)
            .OrderByDescending(group => group.Samples.Count)
            .ThenBy(group => group.StartX)
            .Aggregate(
                new List<double>(),
                (selected, group) =>
                {
                    if (selected.Count < 4 &&
                        selected.All(value => Math.Abs(value - group.StartX) >= minimumSeedDistance - tolerance))
                    {
                        selected.Add(group.StartX);
                    }
                    return selected;
                })
            .OrderBy(value => value)
            .ToArray();

        if (starts.Length is < 2 or > 4)
        {
            return groups.ToList();
        }

        double[] boundaries = starts
            .Zip(starts.Skip(1), (left, right) =>
            {
                double gap = right - left;
                return right - Math.Min(height * 2, gap * 0.15);
            })
            .ToArray();
        var columns = starts.Select(_ => new List<Group>()).ToArray();
        foreach (Group group in groups)
        {
            int index = 0;
            while (index < boundaries.Length && group.StartX > boundaries[index] + tolerance)
            {
                index++;
            }
            columns[index].Add(group);
        }

        if (columns.Any(column => column.Sum(group => group.Samples.Count) < 6))
        {
            return groups.ToList();
        }

        double panelMinimumY = columns
            .SelectMany(column => column)
            .SelectMany(group => group.Samples)
            .Min(sample => sample.Anchor.Y);
        double panelMaximumY = columns
            .SelectMany(column => column)
            .SelectMany(group => group.Samples)
            .Max(sample => sample.Anchor.Y);
        double panelMinimumX = candidates.Min(sample => sample.Anchor.X);
        double panelMaximumX = candidates.Max(sample => sample.Anchor.X);

        return columns
            .Select((column, index) =>
            {
                double lowerX = index == 0
                    ? panelMinimumX
                    : boundaries[index - 1];
                double upperX = index + 1 == columns.Length
                    ? panelMaximumX
                    : boundaries[index];
                NarrativeOccupancySample[] eligible = candidates
                    .Where(sample =>
                        sample.Anchor.X >= lowerX - tolerance &&
                        sample.Anchor.X <= upperX + tolerance &&
                        sample.Anchor.Y >= panelMinimumY - height * 3.50 - tolerance &&
                        sample.Anchor.Y <= panelMaximumY + height * 3.50 + tolerance)
                    .GroupBy(sample => sample.Id, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToArray();
                NarrativeOccupancySample[] samples = SelectNarrativeMembers(
                        column.SelectMany(group => group.Samples).ToArray(),
                        eligible,
                        height,
                        tolerance)
                    .OrderByDescending(sample => sample.Bounds.Center.Y)
                    .ThenBy(sample => sample.Anchor.X)
                    .ToArray();
                return new Group(
                    samples.Average(sample => sample.Anchor.X),
                    Union(samples.Select(sample => sample.Bounds)),
                    samples);
            })
            .ToList();
    }

    private static NarrativeOccupancySample[] SelectNarrativeMembers(
        IReadOnlyList<NarrativeOccupancySample> seeds,
        IReadOnlyList<NarrativeOccupancySample> eligible,
        double height,
        double tolerance)
    {
        var selected = seeds
            .GroupBy(sample => sample.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToDictionary(sample => sample.Id, StringComparer.Ordinal);

        double minimumSeedX = seeds.Min(sample => sample.Anchor.X);
        double maximumSeedX = seeds.Max(sample => sample.Anchor.X);
        double horizontalReach = height * 6;

        foreach (NarrativeOccupancySample sample in eligible.Where(sample =>
                     (IsNarrativeBody(sample.Text) || IsNarrativeHeading(sample.Text)) &&
                     sample.Anchor.X >= minimumSeedX - horizontalReach - tolerance &&
                     sample.Anchor.X <= maximumSeedX + horizontalReach + tolerance))
        {
            selected.TryAdd(sample.Id, sample);
        }

        bool changed;
        do
        {
            changed = false;
            NarrativeOccupancySample[] current = selected.Values.ToArray();
            foreach (NarrativeOccupancySample sample in eligible.Where(sample =>
                         !selected.ContainsKey(sample.Id)))
            {
                if (current.Any(member =>
                        IsSameNarrativeRow(member, sample, height, tolerance) ||
                        IsShortVerticalContinuation(member, sample, height, tolerance)))
                {
                    selected.Add(sample.Id, sample);
                    changed = true;
                }
            }
        } while (changed);

        return selected.Values.ToArray();
    }

    private static bool IsNarrativeBody(string value) =>
        VisibleTextLength(value) >= 16;

    private static bool IsNarrativeHeading(string value)
    {
        string text = VisibleText(value).Trim();
        return (text.EndsWith(':') || text.EndsWith('：')) &&
               text.Count(char.IsLetter) >= 2;
    }

    private static double HorizontalDistance(Rect2 left, Rect2 right) =>
        left.Right < right.Left
            ? right.Left - left.Right
            : right.Right < left.Left
                ? left.Left - right.Right
                : 0;

    private static bool IsSameNarrativeRow(
        NarrativeOccupancySample member,
        NarrativeOccupancySample sample,
        double height,
        double tolerance) =>
        Math.Abs(member.Bounds.Center.Y - sample.Bounds.Center.Y) <=
        height * 0.55 + tolerance &&
        HorizontalDistance(member.Bounds, sample.Bounds) <=
        height * 6 + tolerance;

    private static bool IsShortVerticalContinuation(
        NarrativeOccupancySample member,
        NarrativeOccupancySample sample,
        double height,
        double tolerance) =>
        ((sample.IsLeftAligned && VisibleText(sample.Text).Count(char.IsLetter) >= 2) ||
         IsShortNumberedHeading(sample) ||
         IsShortCenteredInteriorFragment(sample)) &&
        VerticalGap(member.Bounds, sample.Bounds) <= height * 2 + tolerance &&
        HorizontalIntersection(member.Bounds, sample.Bounds) >=
        Math.Min(member.Bounds.Width, sample.Bounds.Width) * 0.50 - tolerance;

    private static int VisibleTextLength(string value)
    {
        int halfUnitWeight = 0;
        foreach (char character in VisibleText(value).Trim())
        {
            halfUnitWeight += character switch
            {
                >= '\u3400' and <= '\u9fff' => 4,
                >= '\uf900' and <= '\ufaff' => 4,
                _ when char.IsLetterOrDigit(character) => 2,
                _ => 1
            };
        }

        return (halfUnitWeight + 1) / 2;
    }

    private static string VisibleText(string value)
    {
        var result = new StringBuilder(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (current is '{' or '}')
            {
                continue;
            }

            if (current == '\\' && index + 1 < value.Length)
            {
                char code = value[index + 1];
                if (code is 'P' or 'p' or 'N' or 'n' or 'X' or 'x')
                {
                    result.Append(' ');
                    index++;
                    continue;
                }

                int terminator = value.IndexOf(';', index + 2);
                if (terminator >= 0)
                {
                    index = terminator;
                    continue;
                }
            }

            result.Append(current);
        }

        return result.ToString();
    }

    private static double VerticalGap(Rect2 left, Rect2 right) =>
        left.Top < right.Bottom
            ? right.Bottom - left.Top
            : right.Top < left.Bottom
                ? left.Bottom - right.Top
                : 0;

    private static IReadOnlyList<IReadOnlyList<Group>> PartitionVerticalPanels(
        IReadOnlyList<Group> groups,
        double height,
        double tolerance)
    {
        var panels = new List<IReadOnlyList<Group>>();
        var current = new List<Group>();
        double currentBottom = 0;
        foreach (Group group in groups
                     .OrderByDescending(group => group.Bounds.Top)
                     .ThenBy(group => group.StartX))
        {
            if (current.Count > 0 &&
                currentBottom - group.Bounds.Top > height * 6 + tolerance)
            {
                panels.Add(current);
                current = [];
            }

            current.Add(group);
            currentBottom = current.Count == 1
                ? group.Bounds.Bottom
                : Math.Min(currentBottom, group.Bounds.Bottom);
        }

        if (current.Count > 0)
        {
            panels.Add(current);
        }

        return panels;
    }

    private static double VerticalDistance(double y, Rect2 bounds) =>
        y > bounds.Top
            ? y - bounds.Top
            : y < bounds.Bottom
                ? bounds.Bottom - y
                : 0;

    private static double Midpoint(double left, double right) => (left + right) / 2;

    private static IReadOnlyList<IReadOnlyList<NarrativeOccupancySample>> BuildStartBands(
        IReadOnlyList<NarrativeOccupancySample> candidates,
        double height,
        double tolerance)
    {
        var bands = new List<IReadOnlyList<NarrativeOccupancySample>>();
        var current = new List<NarrativeOccupancySample>();
        double bandMinimumX = 0;
        foreach (NarrativeOccupancySample candidate in candidates)
        {
            if (current.Count == 0 ||
                candidate.Anchor.X - bandMinimumX <= height * 4 + tolerance)
            {
                if (current.Count == 0)
                {
                    bandMinimumX = candidate.Anchor.X;
                }

                current.Add(candidate);
                continue;
            }

            bands.Add(current);
            current = [candidate];
            bandMinimumX = candidate.Anchor.X;
        }

        if (current.Count > 0)
        {
            bands.Add(current);
        }

        return bands;
    }

    private static IReadOnlyList<IReadOnlyList<NarrativeOccupancySample>> SplitVerticalRuns(
        IReadOnlyList<NarrativeOccupancySample> band,
        double height,
        double tolerance)
    {
        NarrativeOccupancySample[] ordered = band
            .OrderByDescending(sample => sample.Bounds.Center.Y)
            .ThenBy(sample => sample.Anchor.X)
            .ToArray();
        var runs = new List<IReadOnlyList<NarrativeOccupancySample>>();
        var current = new List<NarrativeOccupancySample>();
        double previousY = 0;
        foreach (NarrativeOccupancySample candidate in ordered)
        {
            double y = candidate.Bounds.Center.Y;
            if (current.Count > 0 &&
                previousY - y > height * 3.50 + tolerance)
            {
                runs.Add(current);
                current = [];
            }

            current.Add(candidate);
            previousY = y;
        }

        if (current.Count > 0)
        {
            runs.Add(current);
        }

        return runs;
    }

    private static bool VerticallyOverlaps(
        Rect2 left,
        Rect2 right,
        double tolerance) =>
        Math.Min(left.Top, right.Top) -
        Math.Max(left.Bottom, right.Bottom) > tolerance;

    private static Rect2 Union(IEnumerable<Rect2> values)
    {
        Rect2[] items = values.ToArray();
        return new Rect2(
            items.Min(item => item.Left),
            items.Min(item => item.Bottom),
            items.Max(item => item.Right),
            items.Max(item => item.Top));
    }
}

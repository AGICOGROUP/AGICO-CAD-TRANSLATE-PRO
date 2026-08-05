namespace CadTranslation.Core;

public sealed record LayoutTextBoxSample(
    string Id,
    Rect2 SourceBounds,
    double OriginalTextHeight);

public static class ExclusiveTextBoxAllocator
{
    public static IReadOnlyDictionary<string, Rect2> Allocate(
        Rect2 parent,
        IReadOnlyList<LayoutTextBoxSample> samples,
        double tolerance = 1e-6)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var result = new Dictionary<string, Rect2>(StringComparer.Ordinal);
        foreach (LayoutTextBoxSample sample in samples)
        {
            if (string.IsNullOrWhiteSpace(sample.Id) ||
                sample.OriginalTextHeight <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(samples),
                    "Text-box samples require a stable ID and positive text height.");
            }

            LayoutTextBoxSample[] rowPeers = samples
                .Where(peer => !ReferenceEquals(peer, sample))
                .Where(peer => VerticallyOverlaps(sample.SourceBounds, peer.SourceBounds, tolerance))
                .ToArray();
            LayoutTextBoxSample? leftPeer = rowPeers
                .Where(peer => peer.SourceBounds.Center.X < sample.SourceBounds.Center.X - tolerance)
                .OrderByDescending(peer => peer.SourceBounds.Center.X)
                .FirstOrDefault();
            LayoutTextBoxSample? rightPeer = rowPeers
                .Where(peer => peer.SourceBounds.Center.X > sample.SourceBounds.Center.X + tolerance)
                .OrderBy(peer => peer.SourceBounds.Center.X)
                .FirstOrDefault();

            double left = parent.Left;
            if (leftPeer is not null)
            {
                double boundary = Boundary(leftPeer.SourceBounds, sample.SourceBounds);
                left = Math.Max(
                    parent.Left,
                    boundary + SharedHalfGutter(leftPeer, sample));
            }

            double right = parent.Right;
            if (rightPeer is not null)
            {
                double boundary = Boundary(sample.SourceBounds, rightPeer.SourceBounds);
                right = Math.Min(
                    parent.Right,
                    boundary - SharedHalfGutter(sample, rightPeer));
            }

            if (right - left <= tolerance)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(samples),
                    $"Text-box sample '{sample.Id}' has no positive exclusive width.");
            }

            LayoutTextBoxSample[] columnPeers = samples
                .Where(peer => !ReferenceEquals(peer, sample))
                .Where(peer => HorizontallyOverlaps(sample.SourceBounds, peer.SourceBounds, tolerance))
                .ToArray();
            LayoutTextBoxSample? belowPeer = columnPeers
                .Where(peer => peer.SourceBounds.Center.Y < sample.SourceBounds.Center.Y - tolerance)
                .OrderByDescending(peer => peer.SourceBounds.Center.Y)
                .FirstOrDefault();
            LayoutTextBoxSample? abovePeer = columnPeers
                .Where(peer => peer.SourceBounds.Center.Y > sample.SourceBounds.Center.Y + tolerance)
                .OrderBy(peer => peer.SourceBounds.Center.Y)
                .FirstOrDefault();
            double bottom = parent.Bottom;
            if (belowPeer is not null)
            {
                double boundary = (belowPeer.SourceBounds.Center.Y + sample.SourceBounds.Center.Y) / 2;
                bottom = Math.Max(parent.Bottom, boundary + SharedHalfGutter(belowPeer, sample));
            }

            double top = parent.Top;
            if (abovePeer is not null)
            {
                double boundary = (sample.SourceBounds.Center.Y + abovePeer.SourceBounds.Center.Y) / 2;
                top = Math.Min(parent.Top, boundary - SharedHalfGutter(sample, abovePeer));
            }

            if (top - bottom <= tolerance)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(samples),
                    $"Text-box sample '{sample.Id}' has no positive exclusive height.");
            }

            if (!result.TryAdd(
                    sample.Id,
                    new Rect2(left, bottom, right, top)))
            {
                throw new ArgumentException(
                    $"Duplicate text-box sample ID '{sample.Id}'.",
                    nameof(samples));
            }
        }

        return result;
    }

    private static bool VerticallyOverlaps(
        Rect2 left,
        Rect2 right,
        double tolerance)
    {
        double overlap = Math.Min(left.Top, right.Top) -
                         Math.Max(left.Bottom, right.Bottom);
        return overlap > Math.Min(left.Height, right.Height) * 0.50 - tolerance;
    }

    private static bool HorizontallyOverlaps(
        Rect2 left,
        Rect2 right,
        double tolerance)
    {
        double overlap = Math.Min(left.Right, right.Right) -
                         Math.Max(left.Left, right.Left);
        return overlap > Math.Min(left.Width, right.Width) * 0.50 - tolerance;
    }

    private static double Boundary(Rect2 left, Rect2 right) =>
        (left.Center.X + right.Center.X) / 2;

    private static double SharedHalfGutter(
        LayoutTextBoxSample left,
        LayoutTextBoxSample right) =>
        Math.Min(left.OriginalTextHeight, right.OriginalTextHeight) * 0.05;
}

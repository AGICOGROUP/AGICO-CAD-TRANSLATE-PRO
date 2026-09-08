namespace CadTranslation.Core;

public static class SourceNeighborSlotAllocator
{
    public static IReadOnlyDictionary<string, Rect2> Allocate(
        IReadOnlyList<LayoutTextBoxSample> items,
        double tolerance = 1e-6)
    {
        var result = new Dictionary<string, Rect2>(StringComparer.Ordinal);
        foreach (LayoutTextBoxSample item in items)
        {
            LayoutTextBoxSample[] rowPeers = items
                .Where(peer => !ReferenceEquals(peer, item))
                .Where(peer => VerticallyOverlaps(item.SourceBounds, peer.SourceBounds, tolerance))
                .ToArray();
            LayoutTextBoxSample? leftPeer = rowPeers
                .Where(peer => peer.SourceBounds.Center.X < item.SourceBounds.Center.X - tolerance)
                .OrderByDescending(peer => peer.SourceBounds.Center.X)
                .FirstOrDefault();
            LayoutTextBoxSample? rightPeer = rowPeers
                .Where(peer => peer.SourceBounds.Center.X > item.SourceBounds.Center.X + tolerance)
                .OrderBy(peer => peer.SourceBounds.Center.X)
                .FirstOrDefault();
            double halfGutter = item.OriginalTextHeight * 0.05;
            double normalHorizontalPadding = Math.Min(
                item.SourceBounds.Width * 0.05,
                item.OriginalTextHeight * 0.50);
            double isolatedHorizontalPadding = rowPeers.Length == 0
                ? Math.Max(0, (item.OriginalTextHeight * 5 - item.SourceBounds.Width) / 2)
                : 0;
            double horizontalPadding = Math.Max(
                normalHorizontalPadding,
                isolatedHorizontalPadding);
            double leftLimit = item.SourceBounds.Left - horizontalPadding;
            double rightLimit = item.SourceBounds.Right + horizontalPadding;
            double left = leftPeer is null
                ? leftLimit
                : Math.Min(
                    item.SourceBounds.Left,
                    (leftPeer.SourceBounds.Center.X + item.SourceBounds.Center.X) / 2 + halfGutter);
            double right = rightPeer is null
                ? rightLimit
                : Math.Max(
                    item.SourceBounds.Right,
                    (item.SourceBounds.Center.X + rightPeer.SourceBounds.Center.X) / 2 - halfGutter);
            left = Math.Min(left, item.SourceBounds.Left);
            right = Math.Max(right, item.SourceBounds.Right);

            LayoutTextBoxSample[] columnPeers = items
                .Where(peer => !ReferenceEquals(peer, item))
                .Where(peer => HorizontallyOverlaps(item.SourceBounds, peer.SourceBounds, tolerance))
                .ToArray();
            LayoutTextBoxSample? below = columnPeers
                .Where(peer => peer.SourceBounds.Center.Y < item.SourceBounds.Center.Y - tolerance)
                .OrderByDescending(peer => peer.SourceBounds.Center.Y)
                .FirstOrDefault();
            LayoutTextBoxSample? above = columnPeers
                .Where(peer => peer.SourceBounds.Center.Y > item.SourceBounds.Center.Y + tolerance)
                .OrderBy(peer => peer.SourceBounds.Center.Y)
                .FirstOrDefault();
            double verticalPadding = Math.Min(
                item.SourceBounds.Height * 0.05,
                item.OriginalTextHeight * 0.25);
            double bottomLimit = item.SourceBounds.Bottom - verticalPadding;
            double topLimit = item.SourceBounds.Top + verticalPadding;
            double bottom = below is null
                ? bottomLimit
                : Math.Min(
                    item.SourceBounds.Bottom,
                    (below.SourceBounds.Center.Y + item.SourceBounds.Center.Y) / 2 + halfGutter);
            double top = above is null
                ? topLimit
                : Math.Max(
                    item.SourceBounds.Top,
                    (item.SourceBounds.Center.Y + above.SourceBounds.Center.Y) / 2 - halfGutter);
            bottom = Math.Min(bottom, item.SourceBounds.Bottom);
            top = Math.Max(top, item.SourceBounds.Top);
            result[item.Id] = new Rect2(left, bottom, right, top);
        }

        return result;
    }

    private static bool HorizontallyOverlaps(
        Rect2 left,
        Rect2 right,
        double tolerance) =>
        Math.Min(left.Right, right.Right) - Math.Max(left.Left, right.Left) > tolerance;

    private static bool VerticallyOverlaps(
        Rect2 left,
        Rect2 right,
        double tolerance) =>
        Math.Min(left.Top, right.Top) - Math.Max(left.Bottom, right.Bottom) > tolerance;
}

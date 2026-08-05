using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadTranslation.Contracts;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

internal sealed record CadLayoutText(
    ObjectId ObjectId,
    string RecordId,
    string DefinitionName,
    string EntityHandle,
    string ObjectType,
    string CandidateText,
    bool IsChanged,
    TextLayoutSnapshot Source,
    LayoutRegion? Region);

internal sealed record CadProtectedGeometry(
    ObjectId ObjectId,
    string ObjectType,
    Rect2 Bounds);

internal sealed record CadDefinitionTopology(
    string Name,
    string Handle,
    IReadOnlyList<Segment2> BoundarySegments,
    IReadOnlyList<LayoutRegion> Regions,
    IReadOnlyList<CadLayoutText> Texts,
    IReadOnlyList<CadProtectedGeometry> ProtectedGeometry);

internal sealed record CadLayoutBaseline(
    IReadOnlyList<CadDefinitionTopology> Definitions,
    IReadOnlyList<BlockInstancePath> BlockInstances)
{
    internal IReadOnlyDictionary<ObjectId, CadLayoutText> TextByObjectId =>
        Definitions.SelectMany(definition => definition.Texts)
            .GroupBy(text => text.ObjectId)
            .ToDictionary(group => group.Key, group => group.First());
}

internal static class DrawingTopologyCapture
{
    internal static CadLayoutBaseline Capture(
        Database database,
        Transaction transaction,
        IReadOnlyList<LayoutWriteInput> inputs)
    {
        var inputById = inputs.ToDictionary(input => input.ObjectId);
        var definitions = new List<CadDefinitionTopology>();
        BlockTable blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);

        foreach (ObjectId blockId in blockTable)
        {
            var block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
            if (block.IsFromExternalReference || block.IsFromOverlayReference)
            {
                continue;
            }

            definitions.Add(CaptureDefinition(transaction, block, inputById));
        }

        CaptureOwnedTextOutsideDefinitions(transaction, inputs, definitions);
        CadLayoutBaseline baseline = new(definitions, BlockInstanceWalker.Capture(database, transaction));
        EnsureEveryInputWasCaptured(baseline, inputs);
        return baseline;
    }

    private static CadDefinitionTopology CaptureDefinition(
        Transaction transaction,
        BlockTableRecord block,
        IReadOnlyDictionary<ObjectId, LayoutWriteInput> inputById)
    {
        var segments = new List<Segment2>();
        var closedFrames = new List<Rect2>();
        var blockFrameCandidates = new List<(ObjectId Id, string Layer, Rect2 Bounds)>();
        var textCandidates = new List<(ObjectId Id, string RecordId, string Type, string SourceText, string Candidate, bool IsChanged, TextLayoutSnapshot Source, bool IsLeftAligned)>();
        var protectedGeometry = new List<CadProtectedGeometry>();

        foreach (ObjectId entityId in block)
        {
            DBObject value = transaction.GetObject(entityId, OpenMode.ForRead, false);
            if (value is not Entity entity)
            {
                continue;
            }

            CaptureBoundary(transaction, entity, segments, closedFrames);
            if (entity is BlockReference && TryBounds(entity) is Rect2 blockBounds)
            {
                blockFrameCandidates.Add((entityId, entity.Layer, blockBounds));
            }
            if (TryCaptureText(entity, inputById.GetValueOrDefault(entityId), out var text))
            {
                textCandidates.Add(text);
                continue;
            }

            if (TryBounds(entity) is Rect2 bounds &&
                entity is not Line and not Polyline and not Polyline2d)
            {
                protectedGeometry.Add(new CadProtectedGeometry(
                    entityId,
                    entity.GetRXClass().Name,
                    bounds));
            }
        }

        double medianHeight = Median(textCandidates.Select(text => text.Source.OriginalTextHeight));
        double tolerance = Math.Max(1e-6, medianHeight * 0.10);
        foreach ((ObjectId id, string layer, Rect2 blockBounds) in blockFrameCandidates)
        {
            Rect2 bounds = SelectBlockFrameBounds(
                transaction,
                id,
                blockBounds,
                tolerance);
            int containedTextCount = textCandidates.Count(text =>
                bounds.Contains(text.Source.Bounds, tolerance));
            if (SheetFrameCandidatePolicy.IsCandidate(
                    layer,
                    bounds,
                    medianHeight,
                    containedTextCount) &&
                !closedFrames.Any(frame => SameBounds(frame, bounds, tolerance)))
            {
                closedFrames.Add(bounds);
            }
        }
        var regions = new List<LayoutRegion>();
        regions.AddRange(GridCellDetector.Detect(segments, tolerance));
        regions.AddRange(closedFrames
            .Where(frame => textCandidates.Count(text => frame.Contains(text.Source.Bounds, tolerance)) >= 2)
            .Select((frame, index) => new LayoutRegion(
                $"frame-{block.Handle}-{index + 1}",
                LayoutRegionKind.ClosedFrame,
                frame)));

        NarrativeOccupancySample[] unresolvedNarrative = textCandidates
            .Where(text =>
            {
                LayoutRegion? assigned = RegionAssigner.Assign(text.Source, regions, tolerance);
                return assigned?.Kind is not LayoutRegionKind.TableCell;
            })
            .Select(text => new NarrativeOccupancySample(
                text.RecordId,
                text.Source.Anchor,
                text.Source.Bounds,
                NarrativeClassificationTextPolicy.Select(
                    text.SourceText,
                    text.Candidate),
                text.IsLeftAligned))
            .ToArray();
        NarrativeOccupancyGroup[] occupancyGroups = NarrativeOccupancyEnvelope.Expand(
            NarrativeOccupancyDetector.DetectGroups(
                unresolvedNarrative,
                medianHeight,
                tolerance),
            unresolvedNarrative,
            closedFrames,
            medianHeight,
            tolerance);
        var occupancyByRecordId = new Dictionary<string, LayoutRegion>(StringComparer.Ordinal);
        for (int index = 0; index < occupancyGroups.Length; index++)
        {
            NarrativeOccupancyGroup group = occupancyGroups[index];
            LayoutRegion region = group.Region with
            {
                Id = $"note-column-{block.Handle}-occupancy-{index + 1}"
            };
            regions.Add(region);
            foreach (string recordId in group.MemberIds)
            {
                occupancyByRecordId[recordId] = region;
            }
        }

        CadLayoutText[] texts = textCandidates.Select(text => new CadLayoutText(
            text.Id,
            text.RecordId,
            block.Name,
            ((Entity)transaction.GetObject(text.Id, OpenMode.ForRead, false)).Handle.ToString(),
            text.Type,
            text.Candidate,
            text.IsChanged,
            text.Source,
            occupancyByRecordId.GetValueOrDefault(text.RecordId) ??
            AssignNonNarrativeRegion(text.Source, regions, tolerance))).ToArray();

        return new CadDefinitionTopology(
            block.Name,
            block.Handle.ToString(),
            segments,
            regions,
            texts,
            protectedGeometry);
    }

    private static bool SameBounds(Rect2 left, Rect2 right, double tolerance) =>
        Math.Abs(left.Left - right.Left) <= tolerance &&
        Math.Abs(left.Bottom - right.Bottom) <= tolerance &&
        Math.Abs(left.Right - right.Right) <= tolerance &&
        Math.Abs(left.Top - right.Top) <= tolerance;

    private static Rect2 SelectBlockFrameBounds(
        Transaction transaction,
        ObjectId referenceId,
        Rect2 blockExtents,
        double tolerance)
    {
        if (transaction.GetObject(referenceId, OpenMode.ForRead, false) is not BlockReference reference)
        {
            return blockExtents;
        }

        var frameSegments = new List<Segment2>();
        var definition = (BlockTableRecord)transaction.GetObject(
            reference.BlockTableRecord,
            OpenMode.ForRead);
        foreach (ObjectId entityId in definition)
        {
            if (transaction.GetObject(entityId, OpenMode.ForRead, false) is not Entity entity ||
                !IsFrameLayer(entity.Layer))
            {
                continue;
            }

            switch (entity)
            {
                case Line line:
                    AddTransformedSegment(
                        frameSegments,
                        line.StartPoint,
                        line.EndPoint,
                        reference.BlockTransform);
                    break;
                case Polyline polyline:
                    Point3d[] points = Enumerable.Range(0, polyline.NumberOfVertices)
                        .Select(index => polyline.GetPoint3dAt(index))
                        .ToArray();
                    AddTransformedSegments(
                        frameSegments,
                        points,
                        polyline.Closed,
                        reference.BlockTransform);
                    break;
                case Polyline2d polyline2d:
                    Point3d[] vertices = polyline2d
                        .Cast<ObjectId>()
                        .Select(vertexId => ((Vertex2d)transaction.GetObject(vertexId, OpenMode.ForRead)).Position)
                        .ToArray();
                    AddTransformedSegments(
                        frameSegments,
                        vertices,
                        polyline2d.Closed,
                        reference.BlockTransform);
                    break;
            }
        }

        Rect2[] rectangles = GridCellDetector.Detect(frameSegments, tolerance)
            .Select(region => region.Bounds)
            .ToArray();
        return SheetFrameBoundaryPolicy.Select(blockExtents, rectangles, tolerance);
    }

    private static bool IsFrameLayer(string layer) =>
        layer.Contains("FRAME", StringComparison.OrdinalIgnoreCase) ||
        layer.Contains("图框", StringComparison.Ordinal);

    private static void AddTransformedSegment(
        ICollection<Segment2> segments,
        Point3d start,
        Point3d end,
        Matrix3d transform)
    {
        Point3d worldStart = start.TransformBy(transform);
        Point3d worldEnd = end.TransformBy(transform);
        segments.Add(new Segment2(
            new Point2(worldStart.X, worldStart.Y),
            new Point2(worldEnd.X, worldEnd.Y)));
    }

    private static void AddTransformedSegments(
        ICollection<Segment2> segments,
        IReadOnlyList<Point3d> vertices,
        bool closed,
        Matrix3d transform)
    {
        for (int index = 0; index + 1 < vertices.Count; index++)
        {
            AddTransformedSegment(segments, vertices[index], vertices[index + 1], transform);
        }

        if (closed && vertices.Count > 2)
        {
            AddTransformedSegment(segments, vertices[^1], vertices[0], transform);
        }
    }

    private static bool TryCaptureText(
        Entity entity,
        LayoutWriteInput? input,
        out (ObjectId Id, string RecordId, string Type, string SourceText, string Candidate, bool IsChanged, TextLayoutSnapshot Source, bool IsLeftAligned) text)
    {
        Rect2? bounds = TryBounds(entity) ?? EstimateManifestBounds(input);
        string recordId = input?.Manifest.RecordId ?? $"existing-{entity.Handle}";
        string candidate = input?.RestoredText ?? string.Empty;
        switch (entity)
        {
            case DBText dbText when bounds is not null:
                Point3d anchor = dbText.HorizontalMode == TextHorizontalMode.TextLeft &&
                                 dbText.VerticalMode == TextVerticalMode.TextBase
                    ? dbText.Position
                    : dbText.AlignmentPoint;
                text = (
                    entity.ObjectId,
                    recordId,
                    entity.GetRXClass().Name,
                    dbText.TextString,
                    candidate.Length == 0 ? dbText.TextString : candidate,
                    input?.IsChanged ?? false,
                    new TextLayoutSnapshot(
                        recordId,
                        bounds.Value,
                        new Point2(anchor.X, anchor.Y),
                        Math.Max(dbText.Height, 1e-6)),
                    dbText.HorizontalMode == TextHorizontalMode.TextLeft);
                return true;
            case MText mText when bounds is not null:
                text = (
                    entity.ObjectId,
                    recordId,
                    entity.GetRXClass().Name,
                    mText.Contents,
                    candidate.Length == 0 ? mText.Contents : candidate,
                    input?.IsChanged ?? false,
                    new TextLayoutSnapshot(
                        recordId,
                        bounds.Value,
                        new Point2(mText.Location.X, mText.Location.Y),
                        Math.Max(mText.TextHeight, 1e-6)),
                    mText.Attachment is AttachmentPoint.TopLeft or
                        AttachmentPoint.MiddleLeft or
                        AttachmentPoint.BottomLeft);
                return true;
            case Entity value when input is not null && bounds is not null:
                text = (
                    value.ObjectId,
                    recordId,
                    value.GetRXClass().Name,
                    input.Manifest.RawText,
                    candidate,
                    input.IsChanged,
                    new TextLayoutSnapshot(
                        recordId,
                        bounds.Value,
                        bounds.Value.Center,
                        Math.Max(input.Manifest.Properties.Height, 1e-6)),
                    string.Equals(
                        input.Manifest.Properties.HorizontalMode,
                        "TextLeft",
                        StringComparison.Ordinal));
                return true;
            default:
                text = default;
                return false;
        }
    }

    private static Rect2? EstimateManifestBounds(LayoutWriteInput? input)
    {
        if (input is null)
        {
            return null;
        }

        Point3Snapshot anchor3 = input.Manifest.Properties.HorizontalMode == "TextLeft" &&
                                 input.Manifest.Properties.VerticalMode == "TextBase"
            ? input.Manifest.Geometry.InsertionPoint
            : input.Manifest.Geometry.AlignmentPoint ?? input.Manifest.Geometry.InsertionPoint;
        return TextBoundsEstimator.Estimate(
            new Point2(anchor3.X, anchor3.Y),
            input.Manifest.Properties.Height,
            input.Manifest.Properties.WidthFactor,
            input.Manifest.PlainText,
            input.Manifest.Properties.HorizontalMode,
            input.Manifest.Properties.VerticalMode,
            input.Manifest.Geometry.RotationRadians);
    }

    private static LayoutRegion? AssignNonNarrativeRegion(
        TextLayoutSnapshot source,
        IReadOnlyList<LayoutRegion> regions,
        double tolerance)
    {
        return RegionAssigner.Assign(
            source,
            regions.Where(region => region.Kind != LayoutRegionKind.NoteColumn).ToArray(),
            tolerance);
    }

    private static void CaptureBoundary(
        Transaction transaction,
        Entity entity,
        ICollection<Segment2> segments,
        ICollection<Rect2> closedFrames)
    {
        switch (entity)
        {
            case Line line:
                segments.Add(new Segment2(
                    new Point2(line.StartPoint.X, line.StartPoint.Y),
                    new Point2(line.EndPoint.X, line.EndPoint.Y)));
                break;
            case Polyline polyline:
                CapturePolyline(polyline, segments, closedFrames);
                break;
            case Polyline2d polyline2d:
                CapturePolyline2d(transaction, polyline2d, segments, closedFrames);
                break;
        }
    }

    private static void CapturePolyline(
        Polyline polyline,
        ICollection<Segment2> segments,
        ICollection<Rect2> closedFrames)
    {
        var vertices = Enumerable.Range(0, polyline.NumberOfVertices)
            .Select(polyline.GetPoint2dAt)
            .Select(point => new Point2(point.X, point.Y))
            .ToArray();
        AddSegments(vertices, polyline.Closed, segments);
        if (polyline.Closed && TryBounds(polyline) is Rect2 bounds)
        {
            closedFrames.Add(bounds);
        }
    }

    private static void CapturePolyline2d(
        Transaction transaction,
        Polyline2d polyline,
        ICollection<Segment2> segments,
        ICollection<Rect2> closedFrames)
    {
        var vertices = new List<Point2>();
        foreach (ObjectId vertexId in polyline)
        {
            var vertex = (Vertex2d)transaction.GetObject(vertexId, OpenMode.ForRead);
            vertices.Add(new Point2(vertex.Position.X, vertex.Position.Y));
        }

        AddSegments(vertices, polyline.Closed, segments);
        if (polyline.Closed && TryBounds(polyline) is Rect2 bounds)
        {
            closedFrames.Add(bounds);
        }
    }

    private static void AddSegments(
        IReadOnlyList<Point2> vertices,
        bool closed,
        ICollection<Segment2> segments)
    {
        for (int index = 0; index + 1 < vertices.Count; index++)
        {
            segments.Add(new Segment2(vertices[index], vertices[index + 1]));
        }

        if (closed && vertices.Count > 2)
        {
            segments.Add(new Segment2(vertices[^1], vertices[0]));
        }
    }

    private static Rect2? TryBounds(Entity entity)
    {
        try
        {
            Extents3d extents = entity.GeometricExtents;
            return new Rect2(
                extents.MinPoint.X,
                extents.MinPoint.Y,
                extents.MaxPoint.X,
                extents.MaxPoint.Y);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return null;
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] ordered = values.Where(value => value > 0).OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
        {
            return 1;
        }

        int middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
    }

    private static void EnsureEveryInputWasCaptured(
        CadLayoutBaseline baseline,
        IReadOnlyList<LayoutWriteInput> inputs)
    {
        HashSet<ObjectId> captured = baseline.Definitions
            .SelectMany(definition => definition.Texts)
            .Select(text => text.ObjectId)
            .ToHashSet();
        ObjectId[] missing = inputs
            .Where(input => !captured.Contains(input.ObjectId))
            .Select(input => input.ObjectId)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new CommandProtocolException(
                "layout_topology_incomplete",
                $"{missing.Length} translated text objects were not captured for layout.");
        }
    }

    private static void CaptureOwnedTextOutsideDefinitions(
        Transaction transaction,
        IReadOnlyList<LayoutWriteInput> inputs,
        IList<CadDefinitionTopology> definitions)
    {
        HashSet<ObjectId> captured = definitions
            .SelectMany(definition => definition.Texts)
            .Select(text => text.ObjectId)
            .ToHashSet();
        foreach (LayoutWriteInput input in inputs.Where(input => !captured.Contains(input.ObjectId)))
        {
            if (transaction.GetObject(input.ObjectId, OpenMode.ForRead, false) is not Entity entity ||
                !TryCaptureText(entity, input, out var text))
            {
                continue;
            }

            BlockTableRecord? owner = FindOwningDefinition(transaction, entity);
            if (owner is null)
            {
                continue;
            }

            int definitionIndex = definitions
                .Select((definition, index) => (definition, index))
                .Where(item => string.Equals(item.definition.Name, owner.Name, StringComparison.Ordinal))
                .Select(item => item.index)
                .DefaultIfEmpty(-1)
                .First();
            if (definitionIndex < 0)
            {
                continue;
            }

            CadDefinitionTopology definition = definitions[definitionIndex];
            LayoutRegion? region = AssignNonNarrativeRegion(
                text.Source,
                definition.Regions,
                Math.Max(1e-6, text.Source.OriginalTextHeight * 0.10));
            definitions[definitionIndex] = definition with
            {
                Texts = definition.Texts.Append(new CadLayoutText(
                    text.Id,
                    text.RecordId,
                    owner.Name,
                    entity.Handle.ToString(),
                    text.Type,
                    text.Candidate,
                    text.IsChanged,
                    text.Source,
                    region)).ToArray()
            };
            captured.Add(input.ObjectId);
        }
    }

    private static BlockTableRecord? FindOwningDefinition(Transaction transaction, DBObject value)
    {
        ObjectId ownerId = value.OwnerId;
        var visited = new HashSet<ObjectId>();
        while (!ownerId.IsNull && visited.Add(ownerId))
        {
            DBObject owner = transaction.GetObject(ownerId, OpenMode.ForRead, false);
            if (owner is BlockTableRecord block)
            {
                return block;
            }

            ownerId = owner.OwnerId;
        }

        return null;
    }
}

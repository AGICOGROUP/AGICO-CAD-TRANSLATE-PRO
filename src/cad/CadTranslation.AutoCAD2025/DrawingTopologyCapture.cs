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
        IReadOnlyList<LayoutWriteInput> inputs,
        CadObjectAccess? access = null)
    {
        access ??= new CadObjectAccess(database, transaction);
        var inputById = inputs.ToDictionary(input => input.ObjectId);
        var definitions = new List<CadDefinitionTopology>();
        BlockTable blockTable = access.Read<BlockTable>(database.BlockTableId, "topology-block-table", required: true)!;

        foreach (ObjectId blockId in blockTable)
        {
            var block = access.Read<BlockTableRecord>(blockId, "topology-block", parentHandle: blockTable.Handle.ToString());
            if (block is null) continue;
            if (block.IsFromExternalReference || block.IsFromOverlayReference)
            {
                continue;
            }

            definitions.Add(CaptureDefinition(transaction, block, inputById, access));
        }

        CaptureOwnedTextOutsideDefinitions(transaction, inputs, definitions, access);
        CadLayoutBaseline baseline = new(definitions, BlockInstanceWalker.Capture(database, transaction, access));
        EnsureEveryInputWasCaptured(baseline, inputs);
        return baseline;
    }

    private static CadDefinitionTopology CaptureDefinition(
        Transaction transaction,
        BlockTableRecord block,
        IReadOnlyDictionary<ObjectId, LayoutWriteInput> inputById,
        CadObjectAccess access)
    {
        var segments = new List<Segment2>();
        var closedFrames = new List<Rect2>();
        var blockFrameCandidates = new List<(ObjectId Id, string Layer, Rect2 Bounds)>();
        var textCandidates = new List<(ObjectId Id, string RecordId, string Type, string SourceText, string Candidate, bool IsChanged, TextLayoutSnapshot Source, bool IsLeftAligned)>();
        var protectedGeometry = new List<CadProtectedGeometry>();

        foreach (ObjectId entityId in block)
        {
            LayoutWriteInput? input = inputById.GetValueOrDefault(entityId);
            var entity = access.Read<Entity>(entityId, "topology-entity", block.Name, block.Handle.ToString(),
                input?.Manifest.RecordId, required: input is not null);
            if (entity is null) continue;
            if (entity is DBText or MText &&
                !TopologyCapturePolicy.ShouldCaptureText(block.Name, input is not null))
            {
                continue;
            }

            bool boundaryCaptured = CaptureBoundary(transaction, entity, segments, closedFrames, access, block.Name);
            if (entity is BlockReference && TryBounds(entity) is Rect2 blockBounds)
            {
                blockFrameCandidates.Add((entityId, entity.Layer, blockBounds));
            }
            if (TryCaptureText(entity, input, out var text))
            {
                textCandidates.Add(text);
                continue;
            }

            if (TryBounds(entity) is Rect2 bounds &&
                (!boundaryCaptured || entity is not Line and not Polyline and not Polyline2d))
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
                tolerance, access, block.Name);
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
        foreach (var text in textCandidates)
        {
            var merged = GridCellDetector.DetectContaining(segments, text.Source.Bounds, tolerance);
            if (merged is not null && merged.Bounds.Height <= text.Source.OriginalTextHeight * 8 &&
                merged.Bounds.Width <= text.Source.OriginalTextHeight * 50 &&
                !regions.Any(r => SameBounds(r.Bounds, merged.Bounds, tolerance)))
                regions.Add(merged with { Id = $"merged-cell-{block.Handle}-{regions.Count + 1}" });
        }
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
            access.Read<Entity>(text.Id, "topology-text", block.Name, recordId: text.RecordId, required: true)!.Handle.ToString(),
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
        double tolerance, CadObjectAccess access, string blockName)
    {
        if (access.Read<BlockReference>(referenceId, "frame-reference", blockName) is not { } reference)
        {
            return blockExtents;
        }

        var frameSegments = new List<Segment2>();
        var definition = access.Read<BlockTableRecord>(reference.BlockTableRecord, "frame-definition", blockName, reference.Handle.ToString());
        if (definition is null) return blockExtents;
        foreach (ObjectId entityId in definition)
        {
            if (access.Read<Entity>(entityId, "frame-member", definition.Name, definition.Handle.ToString()) is not { } entity ||
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
                    if (!access.TryVertices(polyline2d.Cast<ObjectId>(), "frame-polyline-vertex", polyline2d.Handle.ToString(), definition.Name, out var vertices))
                        return blockExtents;
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
            case Dimension dimension when input is not null:
                DimStyleTableRecord style = dimension.GetDimstyleData();
                double dimHeight = Math.Max(style.Dimtxt * Math.Max(dimension.Dimscale, 1), 1e-6);
                Point3d position = dimension.TextPosition;
                Rect2 dimBounds = TextBoundsEstimator.Estimate(new Point2(position.X, position.Y), dimHeight, 1,
                    input.Manifest.PlainText, "TextCenter", "TextVerticalMid", input.Manifest.Geometry.RotationRadians);
                text = (entity.ObjectId, recordId, entity.GetRXClass().Name, input.Manifest.RawText, candidate,
                    input.IsChanged, new TextLayoutSnapshot(recordId, dimBounds, new Point2(position.X, position.Y), dimHeight), false);
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

    private static bool CaptureBoundary(
        Transaction transaction,
        Entity entity,
        ICollection<Segment2> segments,
        ICollection<Rect2> closedFrames, CadObjectAccess access, string blockName)
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
                return CapturePolyline2d(transaction, polyline2d, segments, closedFrames, access, blockName);
        }
        return true;
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

    private static bool CapturePolyline2d(
        Transaction transaction,
        Polyline2d polyline,
        ICollection<Segment2> segments,
        ICollection<Rect2> closedFrames, CadObjectAccess access, string blockName)
    {
        if (!access.TryVertices(polyline.Cast<ObjectId>(), "topology-polyline-vertex", polyline.Handle.ToString(), blockName, out var points)) return false;
        var vertices = points.Select(p => new Point2(p.X, p.Y)).ToArray();

        AddSegments(vertices, polyline.Closed, segments);
        if (polyline.Closed && TryBounds(polyline) is Rect2 bounds)
        {
            closedFrames.Add(bounds);
        }
        return true;
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
        if (entity is MText text && CadLayoutGeometry.TryFreshBounds(text) is { } fresh)
            return new Rect2(fresh.MinX, fresh.MinY, fresh.MaxX, fresh.MaxY);
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
        IList<CadDefinitionTopology> definitions, CadObjectAccess access)
    {
        HashSet<ObjectId> captured = definitions
            .SelectMany(definition => definition.Texts)
            .Select(text => text.ObjectId)
            .ToHashSet();
        foreach (LayoutWriteInput input in inputs.Where(input => !captured.Contains(input.ObjectId)))
        {
            if (access.Read<Entity>(input.ObjectId, "topology-owned-text", recordId: input.Manifest.RecordId, required: true) is not { } entity ||
                !TryCaptureText(entity, input, out var text))
            {
                continue;
            }

            BlockTableRecord? owner = FindOwningDefinition(transaction, entity, access);
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
            // Attribute coordinates belong to the containing space, while title-block
            // cell lines belong to the referenced definition. Transform those cells
            // before assigning the attribute's allowed region.
            if (entity is AttributeReference && access.Read<DBObject>(entity.OwnerId, "attribute-owner", parentHandle: entity.Handle.ToString()) is BlockReference reference)
            {
                var referencedBlock = access.Read<BlockTableRecord>(reference.BlockTableRecord, "attribute-definition", parentHandle: reference.Handle.ToString());
                var local = referencedBlock is null ? null : definitions.FirstOrDefault(d => d.Name == referencedBlock.Name);
                if (local is not null)
                {
                    var transformed = local.Regions.Where(r => r.Kind is LayoutRegionKind.TableCell or LayoutRegionKind.ClosedFrame)
                        .Select(r => {
                            var corners = new[] { new Point3d(r.Bounds.Left, r.Bounds.Bottom, 0), new Point3d(r.Bounds.Right, r.Bounds.Bottom, 0),
                                new Point3d(r.Bounds.Right, r.Bounds.Top, 0), new Point3d(r.Bounds.Left, r.Bounds.Top, 0) }
                                .Select(p => p.TransformBy(reference.BlockTransform)).ToArray();
                            return new LayoutRegion($"attribute-{reference.Handle}-{r.Id}", r.Kind,
                                new Rect2(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Max(p => p.X), corners.Max(p => p.Y)));
                        }).ToArray();
                    definition = definition with { Regions = definition.Regions.Concat(transformed)
                        .GroupBy(r => r.Id).Select(g => g.First()).ToArray() };
                }
            }
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

    private static BlockTableRecord? FindOwningDefinition(Transaction transaction, DBObject value, CadObjectAccess access)
    {
        ObjectId ownerId = value.OwnerId;
        var visited = new HashSet<ObjectId>();
        while (!ownerId.IsNull && visited.Add(ownerId))
        {
            DBObject? owner = access.Read<DBObject>(ownerId, "topology-owner", parentHandle: value.Handle.ToString());
            if (owner is null) return null;
            if (owner is BlockTableRecord block)
            {
                return block;
            }

            ownerId = owner.OwnerId;
        }

        return null;
    }
}

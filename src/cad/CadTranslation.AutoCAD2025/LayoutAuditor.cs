using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

internal static class LayoutAuditor
{
    private sealed record CandidateText(
        CadLayoutText Baseline,
        string NewHandle,
        Rect2 Bounds,
        Point2 Anchor,
        IReadOnlyList<string> Actions);

    internal static LayoutAuditReport Audit(
        Database candidate,
        CadLayoutBaseline baseline,
        LayoutOptimizationResult optimization,
        int passIndex,
        bool refineGeometry = false)
    {
        Dictionary<string, LayoutAdjustment> adjustmentByRecord = optimization.Adjustments
            .GroupBy(adjustment => adjustment.RecordId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        Dictionary<string, string> handleMap = optimization.Adjustments
            .Where(adjustment => !string.Equals(adjustment.OldHandle, adjustment.NewHandle, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(adjustment => adjustment.OldHandle, adjustment => adjustment.NewHandle, StringComparer.OrdinalIgnoreCase);

        using Transaction transaction = candidate.TransactionManager.StartTransaction();
        var candidateTexts = new List<CandidateText>();
        foreach (CadLayoutText source in baseline.Definitions.SelectMany(definition => definition.Texts))
        {
            string newHandle = handleMap.GetValueOrDefault(source.EntityHandle, source.EntityHandle);
            Entity entity = ResolveEntity(candidate, transaction, newHandle, source.RecordId);
            bool wasAdjusted = adjustmentByRecord.TryGetValue(source.RecordId, out LayoutAdjustment? adjustment);
            Rect2? resolvedBounds = LayoutAuditBounds.Resolve(
                TryToRect(entity),
                source.Source.Bounds,
                wasAdjusted);
            if (resolvedBounds is not Rect2 bounds)
            {
                throw new CommandProtocolException(
                    "layout_audit_missing_extents",
                    $"Cannot measure adjusted candidate {source.RecordId} at handle {entity.Handle}.");
            }

            Point2 anchor = Anchor(entity, bounds);
            IReadOnlyList<string> actions = wasAdjusted
                ? adjustment!.Actions
                : [];
            candidateTexts.Add(new CandidateText(source, newHandle, bounds, anchor, actions));
        }

        var rows = candidateTexts.Select(text => new LayoutAuditTextRow(
            text.Baseline.RecordId,
            text.Baseline.DefinitionName,
            text.Baseline.Region?.Id ?? string.Empty,
            text.Baseline.EntityHandle,
            text.NewHandle,
            text.Baseline.Source.Anchor,
            text.Anchor,
            text.Baseline.Source.Bounds,
            text.Bounds,
            text.Actions)).ToArray();
        var risks = new List<LayoutAuditRiskRow>();
        AuditWorldContainmentAndAnchors(baseline, candidateTexts, risks);
        AuditLocalTextOverlap(candidateTexts, risks);
        AuditProtectedGeometry(baseline, candidateTexts, risks, candidate, transaction, refineGeometry);

        // Unreferenced definitions are retained and translated, but have no placed
        // geometry in any model/paper layout. Keep their findings as advisory.
        var placedDefinitions = baseline.BlockInstances.Select(instance => instance.DefinitionId)
            .ToHashSet(StringComparer.Ordinal);
        for (int i = 0; i < risks.Count; i++)
        {
            if (!placedDefinitions.Contains(risks[i].DefinitionName))
                risks[i] = risks[i] with { Level = "low", Detail = "Unreferenced block definition (not placed in any layout). " + risks[i].Detail };
        }

        string[] expectedPaths = baseline.BlockInstances
            .Select(instance => instance.Path)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        string[] auditedPaths = baseline.BlockInstances
            .Where(instance => baseline.Definitions.Any(definition =>
                string.Equals(definition.Name, instance.DefinitionId, StringComparison.Ordinal)))
            .Select(instance => instance.Path)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        string[] missingPaths = LayoutInstanceCoverage.Missing(expectedPaths, auditedPaths);
        IReadOnlyDictionary<string, int> counts = Enum.GetNames<LayoutRiskLevel>()
            .ToDictionary(
                name => name.ToLowerInvariant(),
                name => risks.Count(risk => string.Equals(risk.Level, name, StringComparison.OrdinalIgnoreCase)),
                StringComparer.Ordinal);
        transaction.Commit();

        return new LayoutAuditReport(
            passIndex,
            CandidateReopened: true,
            baseline.Definitions.Sum(definition => definition.Regions.Count),
            baseline.Definitions.Sum(definition => definition.Regions.Count(region => region.Kind == LayoutRegionKind.TableCell)),
            baseline.Definitions.Sum(definition => definition.Regions.Count(region => region.Kind == LayoutRegionKind.NoteColumn)),
            expectedPaths.Length,
            auditedPaths.Length,
            missingPaths,
            counts,
            handleMap,
            rows,
            risks,
            risks.Where(risk => LayoutAuditPolicy.RequiresManualReview(risk.Code, risk.Level)).ToArray());
    }

    private static void AuditWorldContainmentAndAnchors(
        CadLayoutBaseline baseline,
        IReadOnlyList<CandidateText> texts,
        ICollection<LayoutAuditRiskRow> risks)
    {
        foreach (CandidateText text in texts.Where(text => text.Baseline.Region is not null))
        {
            LayoutRegion region = text.Baseline.Region!;
            BlockInstancePath[] instances = baseline.BlockInstances
                .Where(instance => string.Equals(
                    instance.DefinitionId,
                    text.Baseline.DefinitionName,
                    StringComparison.Ordinal))
                .ToArray();
            if (instances.Length == 0)
            {
                instances =
                [
                    new BlockInstancePath(
                        text.Baseline.DefinitionName,
                        $"definition:{text.Baseline.DefinitionName}",
                        Transform2.Identity)
                ];
            }

            foreach (BlockInstancePath instance in instances)
            {
                Rect2 worldCandidate = instance.WorldTransform.Apply(text.Bounds);
                Rect2 worldRegion = instance.WorldTransform.Apply(region.Bounds);
                double containmentTolerance = Math.Max(1e-6, text.Baseline.Source.OriginalTextHeight * 0.10);
                bool sourceInsideRegion = region.Bounds.Contains(text.Baseline.Source.Bounds, containmentTolerance);
                bool candidateInsideRegion = worldRegion.Contains(worldCandidate, containmentTolerance);
                if (LayoutCrossRegionPolicy.ShouldReport(
                        text.Baseline.IsChanged,
                        sourceInsideRegion,
                        candidateInsideRegion))
                {
                    risks.Add(new LayoutAuditRiskRow(
                        "cross-region",
                        "high",
                        text.Baseline.RecordId,
                        null,
                        text.Baseline.DefinitionName,
                        region.Id,
                        instance.Path,
                        1,
                        worldCandidate,
                        $"Candidate text exceeds {region.Kind}."));
                }
            }

            if (region.Kind == LayoutRegionKind.TableCell &&
                !TextAnchorPolicy.IsPreserved(
                    text.Baseline.Source.Anchor,
                    text.Anchor,
                    text.Baseline.Source.OriginalTextHeight))
            {
                bool candidateInsideCell = region.Bounds.Contains(
                    text.Bounds,
                    Math.Max(1e-6, text.Baseline.Source.OriginalTextHeight * 0.10));
                LayoutRiskLevel driftLevel =
                    TableCellAnchorDriftPolicy.Classify(candidateInsideCell);
                risks.Add(new LayoutAuditRiskRow(
                    "anchor-drift",
                    driftLevel.ToString().ToLowerInvariant(),
                    text.Baseline.RecordId,
                    null,
                    text.Baseline.DefinitionName,
                    region.Id,
                    instances[0].Path,
                    1,
                    instances[0].WorldTransform.Apply(text.Bounds),
                    candidateInsideCell
                        ? "Table-cell visual anchor moved within its cell beyond 0.15 text height."
                        : "Table-cell visual anchor moved outside its cell beyond 0.15 text height."));
            }
        }
    }

    private static void AuditLocalTextOverlap(
        IReadOnlyList<CandidateText> texts,
        ICollection<LayoutAuditRiskRow> risks)
    {
        Dictionary<string, CandidateText> byRecordId = texts.ToDictionary(
            text => text.Baseline.RecordId,
            StringComparer.Ordinal);
        LayoutOverlapPair[] pairs = LayoutOverlapPairSelector.Select(texts
            .Select(text => new LayoutOverlapItem(
                text.Baseline.RecordId,
                text.Baseline.DefinitionName,
                text.Baseline.Region?.Id ?? string.Empty))
            .ToArray());
        foreach (LayoutOverlapPair pair in pairs)
        {
            CandidateText left = byRecordId[pair.LeftRecordId];
            CandidateText right = byRecordId[pair.RightRecordId];
            if (!LayoutTextOverlapPolicy.ShouldReport(
                    left.Baseline.CandidateText,
                    right.Baseline.CandidateText,
                    left.Baseline.IsChanged,
                    right.Baseline.IsChanged))
            {
                continue;
            }
            double sourceOverlap = IntersectionArea(left.Baseline.Source.Bounds, right.Baseline.Source.Bounds);
            double candidateOverlap = IntersectionArea(left.Bounds, right.Bounds);
            double smallerArea = Math.Min(left.Bounds.Area, right.Bounds.Area);
            LayoutRisk risk = LayoutRiskClassifier.Classify(new LayoutRiskInput(
                LayoutRiskCode.TextOverlap,
                sourceOverlap,
                candidateOverlap,
                smallerArea));
            if (risk.NewOverlapRatio <= 0)
            {
                continue;
            }

            risks.Add(new LayoutAuditRiskRow(
                "text-overlap",
                risk.Level.ToString().ToLowerInvariant(),
                left.Baseline.RecordId,
                right.Baseline.RecordId,
                left.Baseline.DefinitionName,
                left.Baseline.Region?.Id ?? string.Empty,
                $"definition:{left.Baseline.DefinitionName}",
                risk.NewOverlapRatio,
                left.Bounds,
                $"New overlap relative to the source drawing; changed-left={left.Baseline.IsChanged}, changed-right={right.Baseline.IsChanged}."));
        }
    }

    private static void AuditProtectedGeometry(
        CadLayoutBaseline baseline,
        IReadOnlyList<CandidateText> texts,
        ICollection<LayoutAuditRiskRow> risks,
        Database candidate, Transaction transaction, bool refineGeometry)
    {
        foreach (CadDefinitionTopology definition in baseline.Definitions)
        {
            CandidateText[] definitionTexts = texts
                .Where(text => string.Equals(text.Baseline.DefinitionName, definition.Name, StringComparison.Ordinal))
                .ToArray();
            foreach (CandidateText text in definitionTexts)
            {
                foreach (CadProtectedGeometry geometry in definition.ProtectedGeometry)
                {
                    double sourceOverlap = IntersectionArea(text.Baseline.Source.Bounds, geometry.Bounds);
                    double candidateOverlap = IntersectionArea(text.Bounds, geometry.Bounds);
                    LayoutRisk risk = LayoutRiskClassifier.Classify(new LayoutRiskInput(
                        LayoutRiskCode.GeometryOverlap,
                        sourceOverlap,
                        candidateOverlap,
                        Math.Min(text.Bounds.Area, geometry.Bounds.Area)));
                    if (risk.NewOverlapRatio <= 0)
                    {
                        continue;
                    }

                    bool? contact = null;
                    if (refineGeometry)
                    {
                        var id = candidate.GetObjectId(false, geometry.ObjectId.Handle, 0);
                        contact = NativeGeometryContact.Intersects((Entity)transaction.GetObject(id, OpenMode.ForRead), text.Bounds);
                        if (contact == false) continue;
                    }

                    risks.Add(new LayoutAuditRiskRow(
                        contact == true ? "geometry-contact" : "geometry-overlap",
                        risk.Level.ToString().ToLowerInvariant(),
                        text.Baseline.RecordId,
                        null,
                        definition.Name,
                        text.Baseline.Region?.Id ?? string.Empty,
                        $"definition:{definition.Name}",
                        risk.NewOverlapRatio,
                        text.Bounds,
                        $"{(contact == true ? "Confirmed curve contact" : "Unconfirmed bounding-box overlap")} with protected {geometry.ObjectType} at {geometry.ObjectId.Handle}."));
                }
            }
        }
    }

    private static Entity ResolveEntity(
        Database database,
        Transaction transaction,
        string handle,
        string recordId)
    {
        if (!long.TryParse(handle, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long value))
        {
            throw new CommandProtocolException("layout_audit_invalid_handle", $"Invalid handle for {recordId}.");
        }

        try
        {
            ObjectId objectId = database.GetObjectId(false, new Handle(value), 0);
            if (objectId.IsNull ||
                transaction.GetObject(objectId, OpenMode.ForRead, false) is not Entity entity)
            {
                throw new CommandProtocolException("layout_audit_missing_text", $"Candidate text is missing for {recordId}.");
            }

            return entity;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception exception)
        {
            throw new CommandProtocolException("layout_audit_missing_text", $"Candidate text is missing for {recordId}.", exception);
        }
    }

    private static Rect2? TryToRect(Entity entity)
    {
        if (entity is MText mText &&
            CadLayoutGeometry.TryFreshBounds(mText) is Bounds2d actual)
        {
            return new Rect2(actual.MinX, actual.MinY, actual.MaxX, actual.MaxY);
        }

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

    private static Point2 Anchor(Entity entity, Rect2 bounds) => entity switch
    {
        DBText text when text.HorizontalMode == TextHorizontalMode.TextLeft &&
                         text.VerticalMode == TextVerticalMode.TextBase =>
            new Point2(text.Position.X, text.Position.Y),
        DBText text => new Point2(text.AlignmentPoint.X, text.AlignmentPoint.Y),
        MText text => new Point2(text.Location.X, text.Location.Y),
        _ => bounds.Center
    };

    private static double IntersectionArea(Rect2 left, Rect2 right)
    {
        double width = Math.Max(0, Math.Min(left.Right, right.Right) - Math.Max(left.Left, right.Left));
        double height = Math.Max(0, Math.Min(left.Top, right.Top) - Math.Max(left.Bottom, right.Bottom));
        return width * height;
    }
}

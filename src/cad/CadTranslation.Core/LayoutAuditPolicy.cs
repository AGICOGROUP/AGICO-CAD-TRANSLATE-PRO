namespace CadTranslation.Core;

public sealed record HardGateInput(
    int CjkResidualCount,
    int MissingRecordCount,
    bool CandidateReopened,
    bool NonTextSignatureMatches,
    bool TableGridSignatureMatches,
    bool SourceAndWorkingPreserved);

public sealed record HardGateResult(
    bool Passed,
    IReadOnlyList<string> ErrorCodes);

public static class LayoutAuditPolicy
{
    public static HardGateResult EvaluateHardGate(HardGateInput input)
    {
        var errors = new List<string>();
        if (input.CjkResidualCount > 0) errors.Add("cjk_residual");
        if (input.MissingRecordCount > 0) errors.Add("missing_records");
        if (!input.CandidateReopened) errors.Add("candidate_reopen_failed");
        if (!input.NonTextSignatureMatches) errors.Add("non_text_mutation");
        if (!input.TableGridSignatureMatches) errors.Add("table_grid_mutation");
        if (!input.SourceAndWorkingPreserved) errors.Add("source_or_working_mutation");
        return new HardGateResult(errors.Count == 0, errors);
    }

    public static bool CanPublishCandidate(
        HardGateResult hardGate,
        IReadOnlyList<LayoutRisk> layoutRisks)
        => hardGate.Passed &&
           layoutRisks.All(risk => !RequiresManualReview(risk.Code, risk.Level));

    public static bool RequiresManualReview(
        LayoutRiskCode code,
        LayoutRiskLevel level) =>
        level == LayoutRiskLevel.High && code != LayoutRiskCode.GeometryOverlap;

    public static bool RequiresManualReview(string code, string level) =>
        string.Equals(level, "high", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(code, "geometry-overlap", StringComparison.OrdinalIgnoreCase);
}

public static class LayoutCrossRegionPolicy
{
    public static bool ShouldReport(
        bool isChanged,
        bool sourceInsideRegion,
        bool candidateInsideRegion) =>
        !candidateInsideRegion && (isChanged || sourceInsideRegion);
}

public static class LayoutTextOverlapPolicy
{
    public static bool ShouldReport(
        string leftText,
        string rightText,
        bool leftChanged,
        bool rightChanged) =>
        (leftChanged || rightChanged) && ShouldReport(leftText, rightText);

    public static bool ShouldReport(string leftText, string rightText) =>
        !IsStandalonePunctuation(leftText) &&
        !IsStandalonePunctuation(rightText);

    private static bool IsStandalonePunctuation(string value)
    {
        string text = value?.Trim() ?? string.Empty;
        return text.Length > 0 && text.All(character =>
            character is ',' or '.' or ';' or ':' or '，' or '。' or '；' or '：');
    }
}

public sealed record LayoutCorrectionCandidate(
    string RecordId,
    string? OtherRecordId,
    LayoutRiskCode Code,
    LayoutRiskLevel Level);

public static class LayoutCorrectionPolicy
{
    public const int MaximumPasses = 5;
    public const double MinimumReadableHeightScale = 0.50;
    public const double MinimumGroupCorrectionScale = 0.80;

    public static double RestoreSourceTextHeight(
        double currentTextHeight,
        double sourceTextHeight)
    {
        if (currentTextHeight <= 0 || sourceTextHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceTextHeight),
                "Text heights must be positive.");
        }

        return sourceTextHeight;
    }

    public static double MinimumReadableHeight(double sourceTextHeight)
    {
        if (sourceTextHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceTextHeight));
        }

        return sourceTextHeight * MinimumReadableHeightScale;
    }

    public static bool ShouldRunAnotherPass(
        int completedPassIndex,
        IReadOnlyList<LayoutRisk> risks) =>
        completedPassIndex < MaximumPasses &&
        risks.Any(risk => risk.Level == LayoutRiskLevel.High);

    public static string[] SelectAdjustedTextOverlapRecords(
        IReadOnlyList<LayoutCorrectionCandidate> risks,
        IReadOnlyCollection<string> adjustedRecordIds)
    {
        var adjusted = new HashSet<string>(adjustedRecordIds, StringComparer.Ordinal);
        return risks
            .Where(risk =>
                risk.Code == LayoutRiskCode.TextOverlap &&
                risk.Level == LayoutRiskLevel.High)
            .SelectMany(risk => new[] { risk.RecordId, risk.OtherRecordId })
            .Where(recordId => recordId is not null && adjusted.Contains(recordId))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(recordId => recordId, StringComparer.Ordinal)
            .ToArray();
    }

}

public static class LayoutAuditBounds
{
    public static Rect2? Resolve(
        Rect2? measured,
        Rect2 baseline,
        bool wasAdjusted) =>
        measured ?? (wasAdjusted ? null : baseline);
}

public static class LayoutInstanceCoverage
{
    public static string[] Missing(
        IReadOnlyList<string> expectedPaths,
        IReadOnlyList<string> auditedPaths)
    {
        var audited = new HashSet<string>(auditedPaths, StringComparer.Ordinal);
        return expectedPaths
            .Where(path => !audited.Contains(path))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }
}

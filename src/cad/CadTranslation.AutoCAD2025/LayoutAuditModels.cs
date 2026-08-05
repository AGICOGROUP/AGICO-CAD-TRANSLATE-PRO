using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

internal sealed record LayoutAuditTextRow(
    string RecordId,
    string DefinitionName,
    string RegionId,
    string OldHandle,
    string NewHandle,
    Point2 SourceAnchor,
    Point2 CandidateAnchor,
    Rect2 SourceBounds,
    Rect2 CandidateBounds,
    IReadOnlyList<string> Actions);

internal sealed record LayoutAuditRiskRow(
    string Code,
    string Level,
    string RecordId,
    string? OtherRecordId,
    string DefinitionName,
    string RegionId,
    string InstancePath,
    double NewOverlapRatio,
    Rect2 CandidateWorldBounds,
    string Detail);

internal sealed record LayoutAuditReport(
    int PassIndex,
    bool CandidateReopened,
    int RegionCount,
    int TableCellCount,
    int NoteColumnCount,
    int ExpectedBlockInstances,
    int AuditedBlockInstances,
    IReadOnlyList<string> MissingBlockInstancePaths,
    IReadOnlyDictionary<string, int> RiskCounts,
    IReadOnlyDictionary<string, string> HandleMap,
    IReadOnlyList<LayoutAuditTextRow> Texts,
    IReadOnlyList<LayoutAuditRiskRow> Risks,
    IReadOnlyList<LayoutAuditRiskRow> ManualReview);

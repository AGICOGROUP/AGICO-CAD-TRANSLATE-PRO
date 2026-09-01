using CadTranslation.Core;
using CadTranslation.Contracts;

namespace CadTranslation.AutoCAD2025;

internal static class ReplaceImportGate
{
    internal const string ArtifactPrefix = "replace";

    internal static void EnsurePassed(LayoutOptimizationResult layout, LayoutAuditReport audit)
    {
        if (audit.MissingBlockInstancePaths.Count > 0)
            throw new CommandProtocolException("replace_layout_instance_audit_incomplete", "Replace pipeline did not audit every block instance.");
        if (layout.UncoveredRecordIds.Count > 0)
            throw new CommandProtocolException("replace_layout_coverage_incomplete", "Replace pipeline has changed records without layout decisions.");
        if (audit.ManualReview.Count > 0)
            throw new CommandProtocolException("replace_layout_high_risk", "Replace candidate has unresolved high layout risks.");
    }
}

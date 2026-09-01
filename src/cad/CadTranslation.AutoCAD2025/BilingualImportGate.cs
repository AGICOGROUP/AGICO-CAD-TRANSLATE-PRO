using CadTranslation.Core;
using CadTranslation.Contracts;

namespace CadTranslation.AutoCAD2025;

internal static class BilingualImportGate
{
    internal const string ArtifactPrefix = "bilingual";

    internal static void EnsurePassed(LayoutOptimizationResult layout, LayoutAuditReport audit)
    {
        if (audit.MissingBlockInstancePaths.Count > 0)
            throw new CommandProtocolException("bilingual_layout_instance_audit_incomplete", "Bilingual pipeline did not audit every block instance.");
        if (layout.UncoveredRecordIds.Count > 0)
            throw new CommandProtocolException("bilingual_layout_coverage_incomplete", "Bilingual pipeline has changed records without layout decisions.");
        if (audit.ManualReview.Count > 0)
            throw new CommandProtocolException("bilingual_layout_high_risk", "Bilingual candidate has unresolved high layout risks.");
    }
}

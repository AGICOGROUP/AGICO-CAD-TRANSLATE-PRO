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
        // Layout findings are preserved in the audit and assessed on the final
        // rendered candidate. They do not discard an otherwise complete drawing;
        // replacement delivery still requires explicit, hash-bound visual acceptance.
    }
}

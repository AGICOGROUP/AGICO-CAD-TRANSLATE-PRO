using Autodesk.AutoCAD.DatabaseServices;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

internal static class ReplaceImportPipeline
{
    internal const string Version = "replace-v1";

    internal static LayoutOptimizationResult Optimize(
        Database database,
        Transaction transaction,
        IReadOnlyList<LayoutTargetSnapshot> targets,
        CadLayoutBaseline baseline) =>
        LayoutOptimizer.Optimize(database, transaction, targets, baseline);
}

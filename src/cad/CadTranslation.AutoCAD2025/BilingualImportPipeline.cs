using Autodesk.AutoCAD.DatabaseServices;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

internal static class BilingualImportPipeline
{
    internal const string Version = "bilingual-v1";

    internal static LayoutOptimizationResult Optimize(
        Database database,
        Transaction transaction,
        IReadOnlyList<LayoutTargetSnapshot> targets,
        CadLayoutBaseline baseline) =>
        LayoutOptimizerV2.Optimize(database, transaction, targets, baseline);
}

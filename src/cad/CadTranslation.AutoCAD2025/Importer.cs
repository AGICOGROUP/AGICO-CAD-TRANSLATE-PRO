using CadTranslation.Contracts;

namespace CadTranslation.AutoCAD2025;

internal static class Importer
{
    internal static int Run(JobContext context) => context.Config.OutputMode switch
    {
        OutputModePolicy.Replace => ReplaceImportPipeline.Run(context),
        OutputModePolicy.Bilingual => BilingualDrawingImporter.Run(context),
        _ => throw new CommandProtocolException("unknown_pipeline", "Choose replace or bilingual.")
    };
}

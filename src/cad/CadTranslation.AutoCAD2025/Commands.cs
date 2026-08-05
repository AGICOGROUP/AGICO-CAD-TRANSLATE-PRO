using Autodesk.AutoCAD.Runtime;

namespace CadTranslation.AutoCAD2025;

public sealed class Commands
{
    [CommandMethod("CADTRANS_SCAN", CommandFlags.Session)]
    public static void Scan() => Execute("scan", DrawingScanner.Scan);

    [CommandMethod("CADTRANS_EXPORT", CommandFlags.Session)]
    public static void Export() => Execute("export", DrawingExporter.Export);

    [CommandMethod("CADTRANS_IMPORT", CommandFlags.Session)]
    public static void Import() => Execute("import", Importer.Run);

    [CommandMethod("CADTRANS_VERIFY", CommandFlags.Session)]
    public static void Verify() => Execute("verify", DrawingVerifier.Verify);

    [CommandMethod("CADTRANS_COMPOSE", CommandFlags.Session)]
    public static void Compose() => Execute("compose", LogicalFlowPrototype.Run);

    private static void Execute(string operation, Func<JobContext, int> body)
    {
        JobContext? context = null;
        try
        {
            context = JobContext.Load(operation);
            int processed = body(context);
            context.WriteResult(JobContext.Succeeded(context, processed));
        }
        catch (System.Exception exception)
        {
            // AutoCAD Core Console converts an exception escaping CommandMethod into a host-level
            // access violation. Protocol failures must therefore terminate at this boundary after
            // their durable result envelope is written.
            if (context is null && string.Equals(operation, "verify", StringComparison.Ordinal))
            {
                JobContext.TryWriteVerificationFailure(exception);
            }
            JobContext.TryWriteFailure(operation, context, exception);
        }
    }
}

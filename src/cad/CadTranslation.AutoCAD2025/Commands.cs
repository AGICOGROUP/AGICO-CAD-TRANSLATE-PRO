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

    [CommandMethod("CADTRANS_INSPECT", CommandFlags.Session)]
    public static void Inspect() => Execute("inspect", CandidateInspection.Run);

    [CommandMethod("CADTRANS_CORRECT", CommandFlags.Session)]
    public static void Correct() => Execute("correct", LocalCorrectionPipeline.Run);

    // Removes OLE2FRAME picture overlays (untranslatable embedded images) from
    // block definitions. The path is read from CAD_TRANSLATE_DELOLE_TARGET so the
    // console can be pointed at any drawing without a job envelope.
    [CommandMethod("CADTRANS_DELOLE", CommandFlags.Session)]
    public static void DeleteOleFrames()
    {
        string progressLog = System.Environment.GetEnvironmentVariable("CAD_TRANSLATE_PROGRESS_LOG") ?? "";
        string path = System.Environment.GetEnvironmentVariable("CAD_TRANSLATE_DELOLE_TARGET") ?? "";
        string outPath = System.Environment.GetEnvironmentVariable("CAD_TRANSLATE_DELOLE_OUTPUT") ?? "";
        try
        {
            var database = new Autodesk.AutoCAD.DatabaseServices.Database(false, true);
            database.ReadDwgFile(path, Autodesk.AutoCAD.DatabaseServices.FileOpenMode.OpenForReadAndAllShare, false, null);
            int erased = 0;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                database.DisableUndoRecording(true);
                var blockTable = (Autodesk.AutoCAD.DatabaseServices.BlockTable)transaction.GetObject(database.BlockTableId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
                foreach (Autodesk.AutoCAD.DatabaseServices.ObjectId blockId in blockTable)
                {
                    var record = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)transaction.GetObject(blockId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
                    var ids = new System.Collections.Generic.List<Autodesk.AutoCAD.DatabaseServices.ObjectId>();
                    foreach (Autodesk.AutoCAD.DatabaseServices.ObjectId id in record) ids.Add(id);
                    foreach (var id in ids)
                    {
                        var entity = (Autodesk.AutoCAD.DatabaseServices.Entity)transaction.GetObject(id, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
                        if (entity is Autodesk.AutoCAD.DatabaseServices.Ole2Frame)
                        {
                            entity.UpgradeOpen();
                            entity.Erase();
                            erased++;
                        }
                    }
                }
                transaction.Commit();
            }
            database.SaveAs(outPath, Autodesk.AutoCAD.DatabaseServices.DwgVersion.AC1021);
            System.IO.File.AppendAllText(progressLog,
                System.DateTime.Now.ToString("HH:mm:ss.fff") + " delole saved erased=" + erased + " to " + outPath + System.Environment.NewLine);
        }
        catch (System.Exception exception)
        {
            // never let an exception escape a CommandMethod: Core Console converts
            // it into a host-level access violation
            System.IO.File.AppendAllText(progressLog,
                System.DateTime.Now.ToString("HH:mm:ss.fff") + " delole FAILED " + exception.Message + System.Environment.NewLine);
        }
    }

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

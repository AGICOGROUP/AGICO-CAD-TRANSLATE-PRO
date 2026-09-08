using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using CadTranslation.Contracts;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

// Host I/O only: acceptance and mutation belong to the selected pipeline.
internal static class NativeDrawing
{
    internal static T[] ReadRows<T>(string path) => File.ReadLines(path)
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => JsonSerializer.Deserialize<T>(line, JsonDefaults.Options)!).ToArray();

    internal static void Report(JobContext context, string name, object value) =>
        AtomicFile.WriteUtf8(Path.Combine(context.Config.ArtifactDirectory, name), JsonSerializer.Serialize(value, JsonDefaults.Options));

    internal static Database Open(string path)
    {
        var db = new Database(false, true);
        try
        {
            if (Path.GetExtension(path).Equals(".dxf", StringComparison.OrdinalIgnoreCase)) db.DxfIn(path, null);
            else db.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, string.Empty);
            db.CloseInput(true);
            return db;
        }
        catch { db.Dispose(); throw; }
    }

    internal static void Save(Database db, string path)
    {
        if (Path.GetExtension(path).Equals(".dxf", StringComparison.OrdinalIgnoreCase)) db.DxfOut(path, 16, db.OriginalFileVersion);
        else db.SaveAs(path, true, db.OriginalFileVersion, db.SecurityParameters);
    }

    internal static void ExportCandidate(JobContext context)
    {
        string output = context.Config.OutputPath;
        using var db = Open(output);
        var previous = HostApplicationServices.WorkingDatabase;
        try
        {
            HostApplicationServices.WorkingDatabase = db;
            string hash = Hashing.Sha256File(output);
            var derived = context.Derive(context.Config with {
                SourcePath = output, WorkingPath = output, SourceSha256 = hash,
                ManifestPath = Path.Combine(context.Config.ArtifactDirectory, context.Config.OutputMode + "-candidate.jsonl") });
            DrawingExporter.Write(derived, db);
        }
        finally { HostApplicationServices.WorkingDatabase = previous; }
    }
}

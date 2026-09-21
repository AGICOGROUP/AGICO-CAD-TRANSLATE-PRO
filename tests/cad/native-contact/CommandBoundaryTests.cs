using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadTranslation.AutoCAD2025;
using CadTranslation.Contracts;
using CadTranslation.Core;
using System.Text.Json;

public sealed class CommandBoundaryTests
{
    [CommandMethod("CAD_COMMAND_BOUNDARY_TEST")]
    public void Run()
    {
        string? outerReport = Environment.GetEnvironmentVariable("CAD_CONTACT_TEST_REPORT");
        string? previousJob = Environment.GetEnvironmentVariable("CAD_LAYOUT_REVIEW_JOB");
        var results = new List<string>();
        try
        {
            string directory = Path.GetDirectoryName(outerReport!)!;
            foreach (string scenario in new[] { "missing-job", "missing-files", "malformed-config", "unwritable-report", "missing-report" })
            {
                string job = Path.Combine(directory, scenario);
                Directory.CreateDirectory(Path.Combine(job, "config"));
                if (scenario == "malformed-config")
                    File.WriteAllText(Path.Combine(job, "config/export-job.json"), "{");
                string report = Path.Combine(job, "report.json");
                Environment.SetEnvironmentVariable("CAD_LAYOUT_REVIEW_JOB", scenario == "missing-job" ? null : job);
                Environment.SetEnvironmentVariable("CAD_CONTACT_TEST_REPORT", scenario == "missing-report" ? null : scenario == "unwritable-report" ? job : report);
                // Catch outside the command to prove a regression without letting the host crash.
                new ContactTests().ReviewExisting();
                if (scenario != "unwritable-report" && scenario != "missing-report")
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(report));
                    if (document.RootElement.GetProperty("status").GetString() != "failed")
                        throw new InvalidOperationException(scenario + " must produce a failed report");
                    if (scenario == "missing-job" && !document.RootElement.GetProperty("error").GetString()!.Contains("CAD_LAYOUT_REVIEW_JOB"))
                        throw new InvalidOperationException("Missing input must name the missing variable");
                }
                results.Add(scenario);
            }
            string validJob = Path.Combine(directory, "valid-job");
            CreateFreshReviewJob(validJob);
            string validReport = Path.Combine(validJob, "report.json");
            Environment.SetEnvironmentVariable("CAD_LAYOUT_REVIEW_JOB", validJob);
            Environment.SetEnvironmentVariable("CAD_CONTACT_TEST_REPORT", validReport);
            var previousDatabase = HostApplicationServices.WorkingDatabase;
            new ContactTests().ReviewExisting();
            using (var document = JsonDocument.Parse(File.ReadAllText(validReport)))
            {
                if (document.RootElement.GetProperty("status").GetString() != "passed" ||
                    !document.RootElement.GetProperty("risks").EnumerateArray().Any(r => r.GetProperty("code").GetString() == "saved-text-overlap"))
                    throw new InvalidOperationException("Valid review must complete and retain real overlap findings.");
            }
            if (HostApplicationServices.WorkingDatabase != previousDatabase)
                throw new InvalidOperationException("Review must restore the host working database.");
            results.Add("valid-review-retains-risks");
            File.WriteAllText(outerReport!, JsonSerializer.Serialize(new { status = "passed", results }));
        }
        catch (System.Exception error)
        {
            File.WriteAllText(outerReport!, JsonSerializer.Serialize(new { status = "failed", results, error = error.ToString() }));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CAD_LAYOUT_REVIEW_JOB", previousJob);
            Environment.SetEnvironmentVariable("CAD_CONTACT_TEST_REPORT", outerReport);
        }
    }

    private static void CreateFreshReviewJob(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, "config"));
        Directory.CreateDirectory(Path.Combine(directory, "artifacts"));
        string drawing = Path.Combine(directory, "candidate.dwg");
        using var db = new Database(true, true);
        var previous = HostApplicationServices.WorkingDatabase;
        try
        {
            HostApplicationServices.WorkingDatabase = db;
            string[] handles = new string[2];
            using (var tx = db.TransactionManager.StartTransaction())
            {
                var table = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
                var model = (BlockTableRecord)tx.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                for (int i = 0; i < 2; i++)
                {
                    var text = new DBText(); text.SetDatabaseDefaults(db);
                    text.TextString = "Pump"; text.Height = 2; text.Position = new Point3d(i, 0, 0);
                    model.AppendEntity(text); tx.AddNewlyCreatedDBObject(text, true);
                    handles[i] = text.Handle.ToString();
                }
                tx.Commit();
            }
            db.SaveAs(drawing, DwgVersion.Current);
            ManifestRecord Row(int i) => new("1.0", "row" + i, "hash", "model", handles[i], "AcDbText", "TextString", "text", "Pump", "Pump", "Pump", [],
                new(new(i, 0, 0), null, 0, null), new("0", "Standard", 2, 1, "", "", new Dictionary<string, string>()), "input");
            var pair = new BilingualDrawingImporter.Pair("row0", handles[0], handles[1], "Pump", "added", "model", new Rect2(-100, -100, 100, 100), .7);
            File.WriteAllText(Path.Combine(directory, "config/export-job.json"), JsonSerializer.Serialize(new { outputPath = drawing }));
            File.WriteAllText(Path.Combine(directory, "artifacts/bilingual-pairs.json"), JsonSerializer.Serialize(new { pairs = new[] { pair } }, JsonDefaults.Options));
            File.WriteAllLines(Path.Combine(directory, "artifacts/bilingual-candidate.jsonl"), Enumerable.Range(0, 2).Select(i => JsonSerializer.Serialize(Row(i), JsonDefaults.Options)));
        }
        finally { HostApplicationServices.WorkingDatabase = previous; }
    }
}

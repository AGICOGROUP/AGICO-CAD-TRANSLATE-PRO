using System.Globalization;
using System.Text;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using CadTranslation.AutoCAD2025.Adapters;
using CadTranslation.Contracts;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

internal sealed record AdapterContext(Database Database, Transaction Transaction, string FileSha256, string OwnerPath);
internal sealed record TextSlot(string Slot, string TextRole, string RawText, TextGeometry Geometry, TextProperties Properties);

internal static class DrawingExporter
{
    private const string SchemaVersion = "1.0";
    private static readonly ITextAdapter[] Adapters = [new DbTextAdapter(), new MTextAdapter(), new AttributeAdapter(), new DimensionAdapter()];

    internal static int Export(JobContext context)
    {
        Progress("export-start");
        context.VerifySourceAndWorkingHashes();
        Progress("hashes-ok");
        return Write(context, HostApplicationServices.WorkingDatabase);
    }

    // Opt-in phase probe: silent unless CAD_TRANSLATE_PROGRESS_LOG is set. Used to
    // localise pathological walks on proxy-heavy drawings without a profiler.
    internal static void Progress(string message)
    {
        string? path = Environment.GetEnvironmentVariable("CAD_TRANSLATE_PROGRESS_LOG");
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            System.IO.File.AppendAllText(path,
                DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
        }
        catch (System.IO.IOException)
        {
        }
    }

    internal static int Write(JobContext context, Database database)
    {
        var records = new List<ManifestRecord>();
        var unsupported = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var unusedBlocks = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int invisibleAttributeTemplates = 0;
        int walked = 0;
        Progress("write-start mode=" + context.Config.OutputMode);
        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            // Bulk walks over drawings with many entities otherwise hit the undo
            // record segment flush (observed as a hard stall near 50k opened
            // objects on a 121k-entity sheet); export is read-only, so undo
            // recording buys nothing here.
            database.DisableUndoRecording(true);
            // Bilingual additions apply to placed drawing content. Dormant block
            // library definitions are retained in the DWG without translation.
            string[]? activePrefixes = null;
            if (string.Equals(context.Config.OutputMode, "bilingual", StringComparison.OrdinalIgnoreCase))
                activePrefixes = BlockInstanceWalker.Capture(database, transaction)
                    .Select(i => $"ROOT/BLOCK/{i.DefinitionId}/").Distinct().ToArray();
            foreach (WalkItem item in EntityWalker.Walk(database, transaction))
            {
                walked++;
                if (walked % 2000 == 0)
                    Progress("walk " + walked + " " + item.Value.GetType().Name + " " + item.OwnerPath);
                if (walked >= 46000) Progress("body-before " + walked + " " + item.Handle);
                if (activePrefixes is not null && !activePrefixes.Any(p => item.OwnerPath.StartsWith(p, StringComparison.Ordinal)))
                {
                    unusedBlocks[item.OwnerPath] = unusedBlocks.GetValueOrDefault(item.OwnerPath) + 1;
                    continue;
                }
                if (activePrefixes is not null && item.Value is AttributeDefinition attribute)
                {
                    var owner = (BlockTableRecord)transaction.GetObject(attribute.OwnerId, OpenMode.ForRead);
                    // A variable attribute definition inside a block is an edit
                    // template. Its visible instances are AttributeReferences.
                    if (attribute.Invisible || (!attribute.Constant && !owner.IsLayout))
                    {
                        invisibleAttributeTemplates++;
                        continue;
                    }
                }
                if (activePrefixes is not null && item.Value is AttributeReference reference && reference.Invisible)
                    continue;
                ITextAdapter? adapter = Adapters.FirstOrDefault(candidate => candidate.CanHandle(item.Value));
                if (walked >= 46000) Progress("adapter " + walked + " " + (adapter is null ? "null" : adapter.GetType().Name));
                if (adapter is null)
                {
                    string className = item.Value.GetRXClass().Name;
                    if (className.Contains("MLeader", StringComparison.OrdinalIgnoreCase) || className.Contains("Table", StringComparison.OrdinalIgnoreCase))
                        unsupported[className] = unsupported.GetValueOrDefault(className) + 1;
                    continue;
                }
                var adapterContext = new AdapterContext(database, transaction, context.Config.SourceSha256, item.OwnerPath);
                if (walked >= 46000) Progress("read-before " + walked + " " + item.Handle);
                foreach (TextSlot slot in adapter.Read(item.Value, adapterContext)) records.Add(ToRecord(item, slot, context.Config.SourceSha256));
                if (walked >= 46000) Progress("read-after " + walked + " " + item.Handle);
            }
            Progress("walk-done walked=" + walked + " records=" + records.Count);
            if (activePrefixes is not null && context.Config.SourceLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) &&
                (context.Config.TargetLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase) ||
                 context.Config.TargetLanguage.StartsWith("fr", StringComparison.OrdinalIgnoreCase)) &&
                Path.GetFileName(context.Config.ManifestPath)=="manifest.input.jsonl")
            {
                var terms=BilingualTermGroups.Find(records);
                if (terms.Length>0)
                {
                    var inputs=records.Select(r=>new LayoutWriteInput(database.GetObjectId(false,new Handle(ParseHandle(r.Handle)),0),r,r.RawText,false)).ToArray();
                    terms=BilingualTermGroups.Find(records,DrawingTopologyCapture.Capture(database,transaction,inputs),new CadObjectAccess(database,transaction));
                }
                NativeDrawing.Report(context,"bilingual-term-groups.json",new {context.Config.SourceSha256,groups=terms});
            }
            transaction.Commit();
        }
        Progress("commit-done records=" + records.Count);
        var ordered = records.OrderBy(record => record.OwnerPath, StringComparer.Ordinal).ThenBy(record => ParseHandle(record.Handle)).ThenBy(record => record.ObjectType, StringComparer.Ordinal).ThenBy(record => record.Slot, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0) throw new CommandProtocolException("empty_manifest", "Export found no supported translatable text records.");
        if (ordered.Select(record => record.RecordId).Distinct(StringComparer.Ordinal).Count() != ordered.Length) throw new CommandProtocolException("duplicate_record_id", "Export produced duplicate record IDs.");
        string jsonl = string.Concat(ordered.Select(record => JsonSerializer.Serialize(record, JsonDefaults.Options) + "\n"));
        AtomicFile.WriteUtf8(context.Config.ManifestPath, jsonl);
        Progress("manifest-written records=" + ordered.Length);
        if (string.Equals(context.Config.OutputMode, "bilingual", StringComparison.OrdinalIgnoreCase))
            NativeDrawing.Report(context, "bilingual-scope.json", new { scope = "model-and-all-layout-reachable-blocks",
                activeRecordCount = ordered.Length, preservedUnusedBlocks = unusedBlocks,
                invisibleAttributeTemplates,
                runtimeModuleId = typeof(DrawingExporter).Assembly.ManifestModule.ModuleVersionId,
                runtimePath = typeof(DrawingExporter).Assembly.Location });
        AtomicFile.WriteUtf8(Path.Combine(context.Config.ArtifactDirectory, "export-summary.json"), JsonSerializer.Serialize(new { recordCount = ordered.Length, typeCounts = ordered.GroupBy(record => record.ObjectType).ToDictionary(group => group.Key, group => group.Count()), unsupported }, JsonDefaults.Options));
        context.VerifySourceAndWorkingHashes();
        return ordered.Length;
    }

    private static ManifestRecord ToRecord(WalkItem item, TextSlot slot, string fileHash)
    {
        ParsedText parsed = ProtectedText.Parse(slot.RawText);
        string type = item.Value.GetRXClass().Name;
        string recordId = Hashing.Sha256Text($"{fileHash}|{item.OwnerPath}|{item.Handle}|{type}|{slot.Slot}");
        string inputHash = Hashing.Sha256Text($"{recordId}|{slot.RawText}|{parsed.FormatTemplate}|{JsonSerializer.Serialize(slot.Properties, JsonDefaults.Options)}");
        return new ManifestRecord(SchemaVersion, recordId, fileHash, item.OwnerPath, item.Handle, type, slot.Slot, slot.TextRole, slot.RawText, parsed.PlainText, parsed.FormatTemplate, parsed.ProtectedTokens, slot.Geometry, slot.Properties, inputHash);
    }

    private static long ParseHandle(string handle) => long.TryParse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value) ? value : long.MaxValue;
}

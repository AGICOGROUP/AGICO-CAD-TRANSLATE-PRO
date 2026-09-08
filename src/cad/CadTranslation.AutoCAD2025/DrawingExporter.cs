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
        context.VerifySourceAndWorkingHashes();
        return Write(context, HostApplicationServices.WorkingDatabase);
    }

    internal static int Write(JobContext context, Database database)
    {
        var records = new List<ManifestRecord>();
        var unsupported = new SortedDictionary<string, int>(StringComparer.Ordinal);
        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            foreach (WalkItem item in EntityWalker.Walk(database, transaction))
            {
                ITextAdapter? adapter = Adapters.FirstOrDefault(candidate => candidate.CanHandle(item.Value));
                if (adapter is null)
                {
                    string className = item.Value.GetRXClass().Name;
                    if (className.Contains("MLeader", StringComparison.OrdinalIgnoreCase) || className.Contains("Table", StringComparison.OrdinalIgnoreCase))
                        unsupported[className] = unsupported.GetValueOrDefault(className) + 1;
                    continue;
                }
                var adapterContext = new AdapterContext(database, transaction, context.Config.SourceSha256, item.OwnerPath);
                foreach (TextSlot slot in adapter.Read(item.Value, adapterContext)) records.Add(ToRecord(item, slot, context.Config.SourceSha256));
            }
            transaction.Commit();
        }
        var ordered = records.OrderBy(record => record.OwnerPath, StringComparer.Ordinal).ThenBy(record => ParseHandle(record.Handle)).ThenBy(record => record.ObjectType, StringComparer.Ordinal).ThenBy(record => record.Slot, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0) throw new CommandProtocolException("empty_manifest", "Export found no supported translatable text records.");
        if (ordered.Select(record => record.RecordId).Distinct(StringComparer.Ordinal).Count() != ordered.Length) throw new CommandProtocolException("duplicate_record_id", "Export produced duplicate record IDs.");
        string jsonl = string.Concat(ordered.Select(record => JsonSerializer.Serialize(record, JsonDefaults.Options) + "\n"));
        AtomicFile.WriteUtf8(context.Config.ManifestPath, jsonl);
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

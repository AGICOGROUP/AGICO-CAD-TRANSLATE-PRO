using System.Text.Json;
using CadTranslation.Contracts;
using CadTranslation.Core;

try
{
    var paths = new Dictionary<string, string>(StringComparer.Ordinal);
    if (args.Length != 4) throw new ArgumentException("Usage: --manifest PATH --translations PATH");
    for (int index = 0; index < args.Length; index += 2)
    {
        if (args[index] is not ("--manifest" or "--translations") || !paths.TryAdd(args[index], args[index + 1]))
            throw new ArgumentException("Usage: --manifest PATH --translations PATH");
    }

    BatchValidationResult result = TranslationValidator.ValidateBatch(
        ReadJsonLines<ManifestRecord>(paths["--manifest"]),
        ReadJsonLines<TranslationRecord>(paths["--translations"]));
    Console.WriteLine(JsonSerializer.Serialize(result, JsonDefaults.Options));
    return result.IsValid ? 0 : 1;
}
catch (Exception error)
{
    var result = new BatchValidationResult(false,
        [new CommandError("text_validation_execution_failed", error.Message, null, null)]);
    Console.WriteLine(JsonSerializer.Serialize(result, JsonDefaults.Options));
    return 2;
}

static List<T> ReadJsonLines<T>(string path)
{
    var records = new List<T>();
    int lineNumber = 0;
    foreach (string line in File.ReadLines(path))
    {
        lineNumber++;
        if (string.IsNullOrWhiteSpace(line)) continue;
        try
        {
            records.Add(JsonSerializer.Deserialize<T>(line, JsonDefaults.Options)
                ?? throw new JsonException("JSON record must not be null."));
        }
        catch (JsonException error)
        {
            throw new FormatException($"{path}, line {lineNumber}: {error.Message}", error);
        }
    }
    return records;
}

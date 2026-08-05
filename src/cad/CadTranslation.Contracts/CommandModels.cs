namespace CadTranslation.Contracts;

public sealed record JobConfig(
    string SchemaVersion,
    string JobId,
    string Operation,
    string SourcePath,
    string WorkingPath,
    string SourceSha256,
    string ManifestPath,
    string? TranslationPath,
    string OutputPath,
    string ResultPath,
    string ArtifactDirectory,
    string SourceLanguage,
    string TargetLanguage);

public sealed record CommandResult(
    string SchemaVersion,
    string JobId,
    string Operation,
    string Status,
    string SourceSha256,
    int ProcessedRecords,
    IReadOnlyList<string> WarningCodes,
    IReadOnlyList<CommandError> Errors,
    DateTimeOffset FinishedAtUtc);

public sealed record CommandError(string Code, string Message, string? RecordId, string? Handle);

public sealed record TranslationRecord(
    string SchemaVersion,
    string RecordId,
    string InputHash,
    string TranslatedText,
    string ReviewStatus,
    string Reason);

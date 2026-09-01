using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CadTranslation.Contracts;
using CadTranslation.Core;

namespace CadTranslation.AutoCAD2025;

internal sealed partial class JobContext
{
    private const string ConfigEnvironmentVariable = "CADTRANS_JOB_CONFIG";
    private const string DiagnosticDirectoryEnvironmentVariable = "CADTRANS_DIAGNOSTIC_DIRECTORY";
    private const string SchemaVersion = "1.0";

    private JobContext(JobConfig config, string jobRoot)
    {
        Config = config;
        JobRoot = jobRoot;
    }

    internal JobConfig Config { get; }
    internal string JobRoot { get; }

    internal static JobContext Load(string invokedOperation)
    {
        string configPath = Environment.GetEnvironmentVariable(ConfigEnvironmentVariable)
            ?? throw new CommandProtocolException("missing_config", "CADTRANS_JOB_CONFIG is required.");
        if (!Path.IsPathFullyQualified(configPath))
        {
            throw new CommandProtocolException("invalid_config_path", "CADTRANS_JOB_CONFIG must be an absolute path.");
        }

        string fullConfigPath = Path.GetFullPath(configPath);
        if (!File.Exists(fullConfigPath))
        {
            throw new CommandProtocolException("missing_config", "CADTRANS_JOB_CONFIG does not point to an existing file.");
        }

        JobConfig config;
        try
        {
            config = JsonSerializer.Deserialize<JobConfig>(File.ReadAllText(fullConfigPath), JsonDefaults.Options)
                ?? throw new JsonException("Configuration cannot be null.");
            config = config with { OutputMode = OutputModePolicy.Normalize(config.OutputMode) };
        }
        catch (JsonException exception)
        {
            throw new CommandProtocolException("invalid_config", "CADTRANS_JOB_CONFIG must be strict JSON matching schema 1.0.", exception);
        }

        string jobRoot = JobPathPolicy.ResolveJobRoot(fullConfigPath);
        RequireCompleteConfig(config, jobRoot);
        if (!string.Equals(config.SchemaVersion, SchemaVersion, StringComparison.Ordinal))
        {
            throw new CommandProtocolException("unsupported_schema", "schemaVersion must be 1.0.");
        }
        if (!string.Equals(config.Operation, invokedOperation, StringComparison.Ordinal))
        {
            throw new CommandProtocolException("operation_mismatch", $"Config operation '{config.Operation}' does not match '{invokedOperation}'.");
        }
        if (!File.Exists(config.SourcePath))
        {
            throw new CommandProtocolException("missing_source_file", "sourcePath must be an existing file.");
        }
        if (!File.Exists(config.WorkingPath))
        {
            throw new CommandProtocolException("missing_working_file", "workingPath must be an existing file.");
        }
        if (!LowerHexSha256().IsMatch(config.SourceSha256))
        {
            throw new CommandProtocolException("invalid_source_hash", "sourceSha256 must be 64 lower-case hexadecimal characters.");
        }

        VerifySourceAndWorkingHashes(config);

        Directory.CreateDirectory(config.ArtifactDirectory);
        return new JobContext(config, jobRoot);
    }

    internal void WriteResult(CommandResult result) =>
        AtomicFile.WriteUtf8(Config.ResultPath, JsonSerializer.Serialize(result, JsonDefaults.Options));

    internal void VerifySourceAndWorkingHashes() => VerifySourceAndWorkingHashes(Config);

    internal static void TryWriteFailure(string operation, JobContext? context, Exception exception)
    {
        try
        {
            if (context is not null)
            {
                context.WriteResult(Failed(context.Config, operation, exception));
                return;
            }

            FailureRouting? routing = TryReadFailureRouting();
            if (routing is not null)
            {
                var result = new CommandResult(SchemaVersion, routing.JobId, operation, "failed", routing.SourceSha256, 0,
                    Array.Empty<string>(), new[] { ToError(exception) }, DateTimeOffset.UtcNow);
                AtomicFile.WriteUtf8(routing.ResultPath, JsonSerializer.Serialize(result, JsonDefaults.Options));
                return;
            }
        }
        catch (Exception routingFailure)
        {
            TryWriteDiagnostic(operation, exception, routingFailure.Message);
            return;
        }

        TryWriteDiagnostic(operation, exception, "No safe job-owned resultPath was available.");
    }

    internal static void TryWriteVerificationFailure(Exception exception)
    {
        try
        {
            VerificationFailureRouting? routing = TryReadVerificationFailureRouting();
            if (routing is null)
            {
                return;
            }

            CommandError error = ToError(exception);
            var report = new
            {
                schemaVersion = SchemaVersion,
                jobId = routing.JobId,
                status = "failed",
                processedRecords = 0,
                errorCodes = new[] { error.Code },
                errors = new[] { error },
                sourceSha256 = routing.SourceSha256,
                workingSha256 = string.Empty,
                candidateSha256 = string.Empty,
                structuralSignatures = new SortedDictionary<string, string>(StringComparer.Ordinal),
                finishedAtUtc = DateTimeOffset.UtcNow
            };
            AtomicFile.WriteUtf8(Path.Combine(routing.ArtifactDirectory, "verification.json"), JsonSerializer.Serialize(report, JsonDefaults.Options));
        }
        catch (Exception)
        {
            // A malformed or unsafe raw config must not turn the command-failure envelope into a host exception.
        }
    }

    internal static CommandResult Succeeded(JobContext context, int processedRecords) =>
        new(SchemaVersion, context.Config.JobId, context.Config.Operation, "succeeded", context.Config.SourceSha256,
            processedRecords, Array.Empty<string>(), Array.Empty<CommandError>(), DateTimeOffset.UtcNow);

    private static CommandResult Failed(JobConfig config, string operation, Exception exception) =>
        new(SchemaVersion, config.JobId, operation, "failed", config.SourceSha256, 0, Array.Empty<string>(),
            new[] { ToError(exception) }, DateTimeOffset.UtcNow);

    private static CommandError ToError(Exception exception) => exception is CommandProtocolException protocol
        ? new CommandError(protocol.Code, protocol.Message, null, null)
        : new CommandError("command_failed", exception.Message, null, null);

    private static void RequireCompleteConfig(JobConfig config, string jobRoot)
    {
        string?[] required = [config.JobId, config.Operation, config.SourcePath, config.WorkingPath, config.SourceSha256,
            config.ManifestPath, config.OutputPath, config.ResultPath, config.ArtifactDirectory, config.SourceLanguage, config.TargetLanguage,
            config.OutputMode];
        if (required.Any(string.IsNullOrWhiteSpace))
        {
            throw new CommandProtocolException("invalid_config", "Required configuration values must be non-empty.");
        }

        RequireAbsoluteFilePath("sourcePath", config.SourcePath, false, jobRoot);
        RequireAbsoluteFilePath("workingPath", config.WorkingPath, true, jobRoot);
        RequireAbsoluteFilePath("manifestPath", config.ManifestPath, true, jobRoot);
        RequireAbsoluteFilePath("outputPath", config.OutputPath, true, jobRoot);
        RequireAbsoluteFilePath("resultPath", config.ResultPath, true, jobRoot);
        RequireAbsoluteDirectoryPath("artifactDirectory", config.ArtifactDirectory, jobRoot);
        if (config.TranslationPath is not null)
        {
            RequireAbsoluteFilePath("translationPath", config.TranslationPath, true, jobRoot);
        }
    }

    private static void VerifySourceAndWorkingHashes(JobConfig config)
    {
        VerifyDeclaredHash("sourcePath", config.SourcePath, config.SourceSha256);
        VerifyDeclaredHash("workingPath", config.WorkingPath, config.SourceSha256);
    }

    private static void VerifyDeclaredHash(string name, string path, string expectedHash)
    {
        string actualHash = Hashing.Sha256File(path);
        if (!string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
        {
            throw new CommandProtocolException("source_hash_mismatch", $"{name} SHA-256 does not match sourceSha256.");
        }
    }

    private static void RequireAbsoluteFilePath(string name, string path, bool mustBeJobOwned, string jobRoot)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new CommandProtocolException("invalid_config_path", $"{name} must be an absolute path.");
        }
        if (mustBeJobOwned && !IsWithin(jobRoot, path))
        {
            throw new CommandProtocolException("path_outside_job", $"{name} must remain under the job root.");
        }
    }

    private static void RequireAbsoluteDirectoryPath(string name, string path, string jobRoot)
    {
        RequireAbsoluteFilePath(name, path, true, jobRoot);
    }

    private static FailureRouting? TryReadFailureRouting()
    {
        string? configPath = Environment.GetEnvironmentVariable(ConfigEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configPath) || !Path.IsPathFullyQualified(configPath) || !File.Exists(configPath))
        {
            return null;
        }

        string fullConfigPath = Path.GetFullPath(configPath);
        string jobRoot = JobPathPolicy.ResolveJobRoot(fullConfigPath);
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(fullConfigPath));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("resultPath", out JsonElement resultElement) ||
                resultElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string? resultPath = resultElement.GetString();
            if (string.IsNullOrWhiteSpace(resultPath) || !Path.IsPathFullyQualified(resultPath) || !IsWithin(jobRoot, resultPath))
            {
                return null;
            }

            string fullResultPath = Path.GetFullPath(resultPath);
            if (File.Exists(fullResultPath))
            {
                return null;
            }

            string sourceSha256 = ReadOptionalString(root, "sourceSha256") is { } hash && LowerHexSha256().IsMatch(hash) ? hash : string.Empty;
            return new FailureRouting(fullResultPath, ReadOptionalString(root, "jobId") ?? string.Empty, sourceSha256);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static VerificationFailureRouting? TryReadVerificationFailureRouting()
    {
        string? configPath = Environment.GetEnvironmentVariable(ConfigEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configPath) || !Path.IsPathFullyQualified(configPath) || !File.Exists(configPath))
        {
            return null;
        }

        string fullConfigPath = Path.GetFullPath(configPath);
        string jobRoot = JobPathPolicy.ResolveJobRoot(fullConfigPath);
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(fullConfigPath));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("artifactDirectory", out JsonElement artifactElement) ||
                artifactElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string? artifactDirectory = artifactElement.GetString();
            if (string.IsNullOrWhiteSpace(artifactDirectory) || !Path.IsPathFullyQualified(artifactDirectory) || !IsWithin(jobRoot, artifactDirectory))
            {
                return null;
            }

            string sourceSha256 = ReadOptionalString(root, "sourceSha256") is { } hash && LowerHexSha256().IsMatch(hash) ? hash : string.Empty;
            return new VerificationFailureRouting(Path.GetFullPath(artifactDirectory), ReadOptionalString(root, "jobId") ?? string.Empty, sourceSha256);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsWithin(string root, string candidate)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return relative is not ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static void TryWriteDiagnostic(string operation, Exception exception, string routingDetail)
    {
        try
        {
            string directory = GetDiagnosticDirectory();
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "command-failure.json");
            var diagnostic = new { operation, error = ToError(exception), routingDetail, finishedAtUtc = DateTimeOffset.UtcNow };
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, JsonDefaults.Utf8NoBom, leaveOpen: true))
            {
                writer.Write(JsonSerializer.Serialize(diagnostic, JsonDefaults.Options));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            TryWriteHostDiagnostic($"CADTRANS_DIAGNOSTIC={path}");
        }
        catch (Exception finalDiagnosticFailure)
        {
            TryWriteHostDiagnostic($"CADTRANS_DIAGNOSTIC_UNAVAILABLE={finalDiagnosticFailure.GetType().Name}");
        }
    }

    private static void TryWriteHostDiagnostic(string message)
    {
        try { Console.Error.WriteLine(message); } catch (Exception) { }
        try { Trace.WriteLine(message); } catch (Exception) { }
    }

    private static string GetDiagnosticDirectory()
    {
        string? requestedDirectory = Environment.GetEnvironmentVariable(DiagnosticDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(requestedDirectory) && Path.IsPathFullyQualified(requestedDirectory))
        {
            return Path.Combine(Path.GetFullPath(requestedDirectory), Guid.NewGuid().ToString("N"));
        }

        return Path.Combine(Path.GetTempPath(), "CadTranslation", "diagnostics", Guid.NewGuid().ToString("N"));
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex LowerHexSha256();

    private sealed record FailureRouting(string ResultPath, string JobId, string SourceSha256);
    private sealed record VerificationFailureRouting(string ArtifactDirectory, string JobId, string SourceSha256);
}

internal sealed class CommandProtocolException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    internal string Code { get; } = code;
}

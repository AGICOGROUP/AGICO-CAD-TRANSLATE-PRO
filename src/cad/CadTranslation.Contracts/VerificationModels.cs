namespace CadTranslation.Contracts;

public sealed record CandidateTextRecord(
    string RecordId,
    string Handle,
    string ObjectType,
    string Slot,
    string ActualText);

public sealed record VerificationResult(bool IsValid, IReadOnlyList<CommandError> Errors);

/// <summary>Canonical supported-text state; intentionally excludes the translated text content.</summary>
public sealed record TextStructureSignature(
    string Handle,
    string ObjectType,
    string OwnerPath,
    string Layer,
    string Color,
    string LineWeight,
    string Properties);

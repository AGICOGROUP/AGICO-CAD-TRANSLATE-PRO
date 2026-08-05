using CadTranslation.Contracts;
using System.Globalization;

namespace CadTranslation.Core;

public sealed record CandidateIdentityOverride(
    string Handle,
    string ObjectType,
    string Slot);

/// <summary>Pure candidate-content gate shared by the AutoCAD verifier and offline tests.</summary>
public static class CandidateContentVerifier
{
    public static VerificationResult Verify(
        IReadOnlyList<ManifestRecord> manifestRecords,
        IReadOnlyList<TranslationRecord> translationRecords,
        IReadOnlyList<CandidateTextRecord> candidateRecords,
        IReadOnlyDictionary<string, CandidateIdentityOverride>? identityOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(manifestRecords);
        ArgumentNullException.ThrowIfNull(translationRecords);
        ArgumentNullException.ThrowIfNull(candidateRecords);

        var errors = new List<CommandError>();
        BatchValidationResult batch = TranslationValidator.ValidateBatch(manifestRecords, translationRecords);
        errors.AddRange(batch.Errors);
        if (manifestRecords.Any(record => record is null || record.ProtectedTokens is null || record.ProtectedTokens.Any(token => token is null ||
                string.IsNullOrWhiteSpace(token.Marker) || token.Kind is null || token.Raw is null)))
            return new VerificationResult(false, errors);

        var manifestById = manifestRecords
            .Where(record => record is not null && !string.IsNullOrWhiteSpace(record.RecordId))
            .GroupBy(record => record.RecordId, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var translationsById = translationRecords
            .Where(record => record is not null && !string.IsNullOrWhiteSpace(record.RecordId))
            .GroupBy(record => record.RecordId, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var candidatesById = new Dictionary<string, CandidateTextRecord>(StringComparer.Ordinal);

        foreach (CandidateTextRecord candidate in candidateRecords)
        {
            if (candidate is null || string.IsNullOrWhiteSpace(candidate.RecordId))
            {
                errors.Add(Error("candidate_missing_record_id", "Candidate recordId is required.", null, null));
                continue;
            }
            if (!candidatesById.TryAdd(candidate.RecordId, candidate))
            {
                errors.Add(Error("candidate_duplicate_record_id", "Candidate recordId must be unique.", candidate.RecordId, candidate.Handle));
                continue;
            }
            if (!manifestById.ContainsKey(candidate.RecordId))
            {
                errors.Add(Error("candidate_record_unknown", "Candidate recordId is not in the manifest.", candidate.RecordId, candidate.Handle));
            }
        }

        foreach ((string recordId, ManifestRecord manifest) in manifestById)
        {
            if (!translationsById.TryGetValue(recordId, out TranslationRecord? translation) ||
                !candidatesById.TryGetValue(recordId, out CandidateTextRecord? candidate))
            {
                errors.Add(Error("candidate_record_missing", "Every manifest record requires exactly one candidate value.", recordId, manifest.Handle));
                continue;
            }

            string expectedHandle = manifest.Handle;
            string expectedObjectType = manifest.ObjectType;
            string expectedSlot = manifest.Slot;
            if (identityOverrides?.TryGetValue(recordId, out CandidateIdentityOverride? identity) == true)
            {
                if (!IsAllowedLayoutIdentity(manifest, identity))
                {
                    errors.Add(Error(
                        "candidate_layout_identity_invalid",
                        "Layout identity override is not an allowed DBText-to-MText conversion.",
                        recordId,
                        identity.Handle));
                }
                else
                {
                    expectedHandle = identity.Handle;
                    expectedObjectType = identity.ObjectType;
                    expectedSlot = identity.Slot;
                }
            }

            if (!string.Equals(expectedHandle, candidate.Handle, StringComparison.Ordinal))
                errors.Add(Error("candidate_handle_mismatch", "Candidate handle does not match the manifest.", recordId, candidate.Handle));
            if (!ImportTargetContract.HasExactObjectType(expectedObjectType, candidate.ObjectType))
                errors.Add(Error("candidate_object_type_mismatch", "Candidate object type does not match the manifest.", recordId, candidate.Handle));
            if (!string.Equals(expectedSlot, candidate.Slot, StringComparison.Ordinal))
                errors.Add(Error("candidate_slot_mismatch", "Candidate slot does not match the manifest.", recordId, candidate.Handle));

            string expected = TranslationValidator.RestoreProtectedTokensForOutput(translation.TranslatedText ?? string.Empty, manifest.ProtectedTokens);
            string actualText = candidate.ActualText ?? string.Empty;
            if (!string.Equals(expected, actualText, StringComparison.Ordinal))
            {
                errors.Add(Error("candidate_text_mismatch", "Candidate value does not exactly match the restored translation.", recordId, candidate.Handle));
            }

            try
            {
                ParsedText candidateParsed = ProtectedText.Parse(actualText);
                if (!manifest.ProtectedTokens.Select(TranslationValidator.NormalizeProtectedTokenForOutput)
                    .Where(IsStructuralProtectedToken).Select(token => token.Raw)
                    .SequenceEqual(candidateParsed.ProtectedTokens.Where(IsStructuralProtectedToken).Select(token => token.Raw), StringComparer.Ordinal))
                    errors.Add(Error("candidate_protected_token_mismatch", "Candidate protected tokens changed after import.", recordId, candidate.Handle));
                if (!TranslationValidator.HasSameInvariantTokens(manifest.RawText, actualText))
                    errors.Add(Error("candidate_invariant_token_mismatch", "Candidate numeric, unit, or model tokens changed after import.", recordId, candidate.Handle));
            }
            catch (FormatException)
            {
                errors.Add(Error("candidate_text_parse_failed", "Candidate text is not valid protected CAD text.", recordId, candidate.Handle));
            }
        }

        return new VerificationResult(errors.Count == 0, errors);
    }

    private static CommandError Error(string code, string message, string? recordId, string? handle) =>
        new(code, message, recordId, handle);

    private static bool IsAllowedLayoutIdentity(
        ManifestRecord manifest,
        CandidateIdentityOverride identity) =>
        string.Equals(manifest.ObjectType, "AcDbText", StringComparison.Ordinal) &&
        string.Equals(manifest.Slot, "text", StringComparison.Ordinal) &&
        string.Equals(identity.ObjectType, "AcDbMText", StringComparison.Ordinal) &&
        string.Equals(identity.Slot, "contents", StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(identity.Handle);

    private static bool IsStructuralProtectedToken(ProtectedToken token) =>
        token.Kind is "field" or "mtext-brace" or "mtext-code" or "dimension-code" or
            "dimension-placeholder" or "acad-symbol" or "placeholder";
}

public static class LayoutTextNormalization
{
    public static string RemoveGeneratedWidthWrapper(string value)
    {
        if (string.IsNullOrEmpty(value) ||
            !value.StartsWith(@"{\W", StringComparison.Ordinal) ||
            !value.EndsWith('}'))
        {
            return value;
        }

        int separator = value.IndexOf(';', 3);
        if (separator < 0 ||
            !double.TryParse(
                value.AsSpan(3, separator - 3),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double scale) ||
            scale < LayoutFitPolicy.MinimumWidthScale - 1e-9 ||
            scale >= 0.999)
        {
            return value;
        }

        return value.Substring(separator + 1, value.Length - separator - 2);
    }
}

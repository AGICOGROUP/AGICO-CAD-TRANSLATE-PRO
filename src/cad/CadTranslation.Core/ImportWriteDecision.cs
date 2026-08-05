namespace CadTranslation.Core;

/// <summary>Prevents no-op import records from opening CAD objects for write.</summary>
public static class ImportWriteDecision
{
    public static bool NeedsLayout(string sourcePlainText, string translatedText)
    {
        ArgumentNullException.ThrowIfNull(sourcePlainText);
        ArgumentNullException.ThrowIfNull(translatedText);
        return !string.Equals(sourcePlainText, translatedText, StringComparison.Ordinal);
    }

    public static bool NeedsWrite(string currentRawText, string restoredText)
    {
        ArgumentNullException.ThrowIfNull(currentRawText);
        ArgumentNullException.ThrowIfNull(restoredText);
        return !string.Equals(currentRawText, restoredText, StringComparison.Ordinal);
    }
}

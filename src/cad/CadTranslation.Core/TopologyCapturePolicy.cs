namespace CadTranslation.Core;

public static class TopologyCapturePolicy
{
    public static bool ShouldCapture(bool isErased) => !isErased;

    public static bool ShouldCaptureText(string definitionName, bool hasLayoutInput) =>
        hasLayoutInput || !definitionName.StartsWith("*D", StringComparison.OrdinalIgnoreCase);
}

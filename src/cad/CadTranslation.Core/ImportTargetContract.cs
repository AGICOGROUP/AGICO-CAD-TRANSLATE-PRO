namespace CadTranslation.Core;

/// <summary>Guards the immutable export identity before a CAD object is opened for write.</summary>
public static class ImportTargetContract
{
    public static bool HasExactObjectType(string? manifestObjectType, string? actualRxClassName) =>
        !string.IsNullOrWhiteSpace(manifestObjectType) &&
        !string.IsNullOrWhiteSpace(actualRxClassName) &&
        string.Equals(manifestObjectType, actualRxClassName, StringComparison.Ordinal);
}

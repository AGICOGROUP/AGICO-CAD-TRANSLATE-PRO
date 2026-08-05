namespace CadTranslation.Core;

public static class FixedLabelInPlaceScalePolicy
{
    public static bool Allows(string objectType) => objectType is
        "AcDbText" or
        "AcDbAttribute" or
        "AcDbAttributeDefinition";
}

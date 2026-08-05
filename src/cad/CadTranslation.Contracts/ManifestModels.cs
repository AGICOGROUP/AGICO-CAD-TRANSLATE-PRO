namespace CadTranslation.Contracts;

public sealed record ManifestRecord(
    string SchemaVersion,
    string RecordId,
    string FileSha256,
    string OwnerPath,
    string Handle,
    string ObjectType,
    string Slot,
    string TextRole,
    string RawText,
    string PlainText,
    string FormatTemplate,
    IReadOnlyList<ProtectedToken> ProtectedTokens,
    TextGeometry Geometry,
    TextProperties Properties,
    string InputHash);

public sealed record ProtectedToken(string Marker, string Kind, string Raw);

public sealed record Point3Snapshot(double X, double Y, double Z);

public sealed record Bounds3Snapshot(Point3Snapshot Minimum, Point3Snapshot Maximum);

public sealed record TextGeometry(
    Point3Snapshot InsertionPoint,
    Point3Snapshot? AlignmentPoint,
    double RotationRadians,
    Bounds3Snapshot? Extents);

public sealed record TextProperties(
    string Layer,
    string TextStyle,
    double Height,
    double WidthFactor,
    string HorizontalMode,
    string VerticalMode,
    IReadOnlyDictionary<string, string> TypeSpecific);

public sealed record ParsedText(
    string PlainText,
    string FormatTemplate,
    IReadOnlyList<ProtectedToken> ProtectedTokens);

public sealed record BatchValidationResult(bool IsValid, IReadOnlyList<CommandError> Errors);

public sealed record LayoutAdjustment(
    double WidthFactorScale,
    double HeightScale,
    double? MTextBoundaryWidth,
    IReadOnlyList<int> InsertLineBreakBeforeCharacter,
    bool RequiresManualReview);

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CadTranslation.AutoCAD2025;
using CadTranslation.Core;
using System.Reflection;
using System.Text.Json;

public class WrapPreferenceTests
{
    [CommandMethod("CAD_WRAP_PREFERENCE_TEST")]
    public void NearbyWideSpace() => Run("single-line");

    [CommandMethod("CAD_WRAP_WIDE_POCKET_TEST")]
    public void WidePocket() => Run("two-line");

    [CommandMethod("CAD_WRAP_DENSE_HINTS_TEST")]
    public void DenseHints() => Run("dense-hints");

    private static void Run(string scenario)
    {
        string report = Environment.GetEnvironmentVariable("CAD_CONTACT_TEST_REPORT")!;
        try
        {
            using var text = new MText();
            text.SetDatabaseDefaults();
            text.Attachment = AttachmentPoint.TopLeft;
            const string contents = "2YA3060 Circular Vibrating Screen";
            var box = new Rect2(0, 0, 10, 1.5);
            var source = new CadLayoutText(ObjectId.Null, "wrap", "model", "1", "AcDbMText", contents, true,
                new TextLayoutSnapshot("wrap", box, box.Center, 1.5), null);
            var definition = new CadDefinitionTopology("model", "1", [], [], [source], []);
            List<Rect2> occupied;
            if (scenario == "single-line")
            {
                // Centered anchors are blocked; nearby edge-aligned space is clear.
                occupied = [box, new(-5, -8, -1, 8), new(10.2, -1.7, 45, 8), new(0, 1.6, 10, 8)];
            }
            else if (scenario == "two-line")
            {
                // Wider than the Chinese source, but not wide enough for one line.
                occupied = [box, new(-100, -100, 1, 100), new(18, -100, 100, 100), new(0, 1.6, 18, 100)];
            }
            else
            {
                // Diagonal segment boxes contain the source but actual lines are far away.
                // They must not evict the useful block edge from the bounded seed list.
                var lines = Enumerable.Range(0, 32).Select(i => new Segment2(
                    new Point2(-100, -50 - i * .001), new Point2(100, 150 - i * .001)))
                    .Append(new Segment2(new Point2(18, -100), new Point2(18, 100))).ToArray();
                definition = definition with {
                    BoundarySegments = lines,
                    ProtectedGeometry = [new CadProtectedGeometry(ObjectId.Null, "AcDbBlockReference", new Rect2(-100, -100, 1, 100))]
                };
                occupied = [box, new(0, 1.6, 18, 100)];
            }
            object?[] args = { text, contents, source, definition, occupied, 0d, box, 0d, new BilingualPlacementTrace() ,false};
            bool placed = (bool)typeof(BilingualDrawingImporter)
                .GetMethod("Place", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;
            var bounds = (Rect2)args[6]!;
            double maxHeightRatio = scenario == "single-line" ? 1.6 : 3.3;
            if (!placed || text.Text != contents || text.ActualHeight > text.TextHeight * maxHeightRatio || text.TextHeight < 1.05 - 1e-8)
                throw new InvalidOperationException($"{scenario}: avoid gratuitous wrapping: placed={placed}, height={text.TextHeight}, actualHeight={text.ActualHeight}, width={text.Width}");
            if (occupied.Any(o => BilingualDrawingImporter.Intersects(bounds, o, .18)))
                throw new InvalidOperationException("Readable placement must retain text clearance; line crossings are allowed in bilingual mode.");
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", scenario, bounds, text.Width, text.TextHeight, text.ActualHeight }));
        }
        catch (System.Exception e)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", scenario, error = e.ToString() }));
        }
    }
}

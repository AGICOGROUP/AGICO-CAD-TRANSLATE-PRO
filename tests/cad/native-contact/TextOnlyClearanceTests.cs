using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CadTranslation.AutoCAD2025;
using CadTranslation.Core;
using CadTranslation.Contracts;
using System.Text.Json;

public class TextOnlyClearanceTests
{
    [CommandMethod("CAD_TEXT_ONLY_CLEARANCE_TEST")]
    public void Run()
    {
        string report = Environment.GetEnvironmentVariable("CAD_CONTACT_TEST_REPORT")!;
        try
        {
            var row=new ManifestRecord("1.0","source","fixture","model","1","AcDbText","TextString","text","水泵","水泵","水泵",[],
                new(new(10,0,0),null,0,null),new("0","Standard",2,1,"","",new Dictionary<string,string>()),"source");
            var number=row with {RecordId="number",Handle="2",RawText="1",Geometry=row.Geometry with {InsertionPoint=new(2,0,0)}};
            var pair=new BilingualDrawingImporter.Pair("source","1","3","1. Water Pump","added","model",new(0,0,1,1),1,"table-aligned-block");
            if(!CandidateInspection.MatchesNumberedTableTarget(pair,"Water Pump",row,[row,number]) ||
                CandidateInspection.MatchesNumberedTableTarget(pair with {TargetText="2. Water Pump"},"Water Pump",row,[row,number]) ||
                CandidateInspection.MatchesNumberedTableTarget(pair with {TargetText="1. Water Valve"},"Water Pump",row,[row,number]) ||
                CandidateInspection.MatchesNumberedTableTarget(pair,"Water Pump",row,[row,number with {OwnerPath="other"}]) ||
                CandidateInspection.MatchesNumberedTableTarget(pair,"Water Pump",row,[row,number with {Geometry=number.Geometry with {InsertionPoint=new(2,100,0)}}]))
                throw new InvalidOperationException("Numbered panel binding must prove the source row identifier and exact approved translation.");
            using var text = new MText();
            text.SetDatabaseDefaults(); text.Attachment = AttachmentPoint.TopLeft;
            text.TextHeight = 2; text.Contents = "Air Flow";
            var original = new Rect2(0, 0, 10, 2);
            var source = new CadLayoutText(ObjectId.Null, "source", "model", "1", "AcDbMText", "气流", true,
                new TextLayoutSnapshot("source", original, original.Center, 2), null);
            var definition = new CadDefinitionTopology("model", "1",
                [new Segment2(new Point2(-100, 4), new Point2(100, 4))], [], [source],
                [new CadProtectedGeometry(ObjectId.Null, "AcDbLine", new Rect2(-100, 3, 100, 6))]);
            var footprint = BilingualPlacementChecks.Footprint(text);
            BilingualPlacementChecks.Move(text, footprint, 0, 6, 0);
            var proposed = new Rect2(0, 6-footprint.Height, footprint.Width, 6);
            if (!BilingualPlacementChecks.Accept(text, proposed, new Rect2(-100,-100,100,100), source,
                definition, [original], .1, null, out var actual))
                throw new InvalidOperationException("Bilingual line crossings must not reject readable text.");
            if (BilingualPlacementChecks.Accept(text, proposed, new Rect2(-100,-100,100,100), source,
                definition, [original, actual], .1, null, out _))
                throw new InvalidOperationException("Existing or added text must still block placement.");
            File.WriteAllText(report, JsonSerializer.Serialize(new {status="passed"}));
        }
        catch (System.Exception e) { File.WriteAllText(report, JsonSerializer.Serialize(new {status="failed",error=e.ToString()})); }
    }
}

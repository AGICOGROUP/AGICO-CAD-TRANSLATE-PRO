using CadTranslation.Core;

var tests = new (string Name, Func<bool> Run)[]
{
    ("drawing number abbreviation", () => TranslationValidator.HasSameInvariantTokens("图号09", "Drawing No.09")),
    ("drawing number changed", () => !TranslationValidator.HasSameInvariantTokens("图号09", "Drawing No.10")),
    ("model changed", () => !TranslationValidator.HasSameInvariantTokens("MDPX150", "MDPX160")),
    ("tracking code protected", () => ProtectedText.Parse(@"\T1.001;%%C14@100双层双向").ProtectedTokens[0].Kind == "mtext-code"),
    ("prose after number", () => TranslationValidator.HasSameInvariantTokens("1、说明", "1General note")),
    ("quantity unchanged", () => TranslationValidator.HasSameInvariantTokens("δ20,5块", "δ20,5 pieces")),
    ("quantity changed", () => !TranslationValidator.HasSameInvariantTokens("δ20,5块", "δ20,6 pieces")),
    ("unit changed", () => !TranslationValidator.HasSameInvariantTokens("压力1.6MPa", "Pressure 1.6kPa")),
    ("temperature unit changed", () => !TranslationValidator.HasSameInvariantTokens("20°C", "20°F")),
    ("pressure unit changed", () => !TranslationValidator.HasSameInvariantTokens("20bar", "20psi")),
    ("force prefix changed", () => !TranslationValidator.HasSameInvariantTokens("10kN", "10MN")),
    ("time prefix changed", () => !TranslationValidator.HasSameInvariantTokens("10ms", "10ks")),
    ("power prefix changed", () => !TranslationValidator.HasSameInvariantTokens("10MW", "10GW")),
    ("prefix case is significant", () => !TranslationValidator.HasSameInvariantTokens("10mW", "10MW")),
    ("torque prefix changed", () => !TranslationValidator.HasSameInvariantTokens("10kNm", "10N·m")),
    ("prefixed force protected", () => ProtectedText.Parse("10kN").ProtectedTokens.Single().Raw == "10kN"),
    ("prefixed torque protected", () => ProtectedText.Parse("10kNm").ProtectedTokens.Single().Raw == "10kNm"),
    ("chinese force unit equivalent", () => TranslationValidator.HasSameInvariantTokens("10牛顿", "10N")),
    ("concrete grade is not coulomb suffix", () => TranslationValidator.HasSameInvariantTokens("300，C15", "300 C15")),
    ("model prefix changed", () => !TranslationValidator.HasSameInvariantTokens("MDPX150", "ABCD150")),
    ("parser does not protect prose as unit", () => ProtectedText.Parse("1 General note").ProtectedTokens[0].Raw == "1"),
};
int failures = 0;
foreach (var test in tests)
{
    try { if (!test.Run()) throw new Exception("assertion failed"); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {error.Message}"); }
}
return failures == 0 ? 0 : 1;

using System.Text.Json;
using CadTranslation.Contracts;

internal static class NativeTestCommand
{
    // Like the production command boundary, diagnostics must never throw into
    // AutoCAD's CommandThunk: even a missing path can invoke CER and abort the host.
    internal static void Execute(Func<object> body)
    {
        string? report = Environment.GetEnvironmentVariable("CAD_CONTACT_TEST_REPORT");
        object result;
        try
        {
            if (string.IsNullOrWhiteSpace(report) || !Path.IsPathFullyQualified(report))
                throw new InvalidOperationException("CAD_CONTACT_TEST_REPORT must name an absolute report file.");
            result = body();
        }
        catch (System.Exception exception)
        {
            result = new { status = "failed", error = exception.ToString() };
        }

        try
        {
            if (string.IsNullOrWhiteSpace(report) || !Path.IsPathFullyQualified(report))
                throw new InvalidOperationException("CAD_CONTACT_TEST_REPORT is unavailable.");
            string json = JsonSerializer.Serialize(result, JsonDefaults.Options);
            string pending = report + ".pending";
            File.WriteAllText(pending, json);
            File.Move(pending, report!, overwrite: true);
        }
        catch (System.Exception reportError)
        {
            // A failed error-report write must not become another unhandled exception.
            try { Console.Error.WriteLine("CAD_TEST_REPORT_FAILED: " + reportError.Message); }
            catch (System.Exception) { }
        }
    }
}

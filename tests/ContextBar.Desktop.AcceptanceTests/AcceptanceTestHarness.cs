using Xunit;

namespace ContextBar.Desktop.AcceptanceTests;

// These scenarios need isolated settings, a verified candidate, synthetic inputs,
// and assertions on outputs and source preservation before they can supply evidence.
// Never attach to a user's process or infer workflow success from a window.
public class AcceptanceTestHarness
{
    [Theory(Skip = "Blocked: isolated workflow automation and artifact verification are not implemented.")]
    [InlineData("ImagePDFZIP")]
    [InlineData("SettingsFallback")]
    [InlineData("ConsecutiveActions")]
    [InlineData("Cancellation")]
    [InlineData("ExcelExport")]
    public void DesktopWorkflow_Acceptance_Harness(string scenario)
    {
        throw new System.NotSupportedException($"Acceptance workflow is not implemented: {scenario}");
    }
}

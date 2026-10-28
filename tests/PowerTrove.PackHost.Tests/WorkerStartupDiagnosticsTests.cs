using PowerTrove.PackHost;
using Xunit;

namespace PowerTrove.PackHost.Tests;

public sealed class WorkerStartupDiagnosticsTests
{
    [Fact]
    public void DiagnosticsExcludeExceptionMessagesPathsAndUnknownStages()
    {
        const string sensitive = "private-generated-content-C:\\private\\selected.txt";
        foreach (var error in new Exception[] { new Exception(sensitive), new FileNotFoundException(sensitive, sensitive), new InvalidDataException(sensitive) })
        {
            string diagnostic = WorkerStartupDiagnostics.Describe(sensitive, error);
            Assert.DoesNotContain(sensitive, diagnostic);
            Assert.Contains("stage=unknown;", diagnostic);
            Assert.Matches(@"^Worker startup: stage=unknown; category=[A-Za-z]+; reason=unspecified; hresult=0x[0-9A-F]{8}$", diagnostic);
        }
    }

    [Fact]
    public void KnownManifestFailureHasAStableCodeWithoutExceptionText()
    {
        string diagnostic = WorkerStartupDiagnostics.Describe("loadPack", new InvalidDataException("Pack factory does not match its manifest."));
        Assert.Contains("category=invalidData; reason=factoryIdentity;", diagnostic);
        Assert.DoesNotContain("Pack factory", diagnostic);
    }
}

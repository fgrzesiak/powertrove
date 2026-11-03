using System;
using System.IO;
using PowerTrove.Application.Localization;
using Xunit;

namespace PowerTrove.Application.Tests.Localization;

public sealed class BrowserSetupHintStateTests
{
    [Fact]
    public void ShownOncePersistsAcrossCallsAndInstances()
    {
        string root = Path.Combine(Path.GetTempPath(), "pt-browser-hint-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(BrowserSetupHintState.TryMarkShown(root));
            Assert.False(BrowserSetupHintState.TryMarkShown(root));
            Assert.False(BrowserSetupHintState.TryMarkShown(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

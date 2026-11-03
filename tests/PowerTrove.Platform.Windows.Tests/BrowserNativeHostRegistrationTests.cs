using PowerTrove.Platform.Windows.Browser;

namespace PowerTrove.Platform.Windows.Tests;

public class BrowserNativeHostRegistrationTests
{
    [Fact]
    public void OnlyExactExtensionOriginsAreAccepted()
    {
        var id = new string('a', 32);
        Assert.True(BrowserNativeHostRegistration.ValidOrigin("chrome-extension://" + id + "/"));
        Assert.False(BrowserNativeHostRegistration.ValidOrigin("chrome-extension://" + id));
        Assert.False(BrowserNativeHostRegistration.ValidOrigin("chrome-extension://" + new string('z', 32) + "/"));
        Assert.False(BrowserNativeHostRegistration.ValidOrigin("chrome-extension://*/"));
        Assert.False(BrowserNativeHostRegistration.ValidOrigin("https://" + id + "/"));
    }
}

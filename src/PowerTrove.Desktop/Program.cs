using PowerTrove.Core.Activation;
using PowerTrove.Platform.Windows.Activation;
using Microsoft.UI.Dispatching;

namespace PowerTrove.Desktop;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is [SetupPackCommand.Argument]) return SetupPackCommand.Run();
        var message = ActivationProtocol.ParseArguments(args);
        if (message is null) return 2;
        using var elector = new WindowsHostElector();
        HostStartupResult result;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
        {
            try { result = HostStartup.ResolveAsync(elector, message, timeout.Token).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { return 3; }
        }
        if (result != HostStartupResult.Host) return result == HostStartupResult.Forwarded ? 0 : 3;
        // Election and forwarding complete before COM wrappers, WinUI, windows, tray or hotkey creation.
        WinRT.ComWrappersSupport.InitializeComWrappers();
        global::Microsoft.UI.Xaml.Application.Start(initialization =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App(elector, message);
        });
        return 0;
    }
}

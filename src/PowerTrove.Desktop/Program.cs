using PowerTrove.Core.Activation;
using PowerTrove.Platform.Windows.Activation;
using Microsoft.UI.Dispatching;

namespace PowerTrove.Desktop;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var distribution = PowerTrove.Platform.Windows.Deployment.WindowsAppDistribution.Current;
        if (!distribution.AllowsSetupMaintenance && args.Length > 0 &&
            args[0] is SetupPackCommand.Argument or SetupShellCommand.Argument) return 2;
        if (args is [SetupPackCommand.Argument]) return SetupPackCommand.Run();
        if (args is [SetupShellCommand.Argument, var operation, var language]) return SetupShellCommand.Run(operation, language);
        var message = ActivationProtocol.ParseArguments(args);
        if (message is null) return 2;
        using var elector = new WindowsHostElector(distribution.InstanceKey);
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

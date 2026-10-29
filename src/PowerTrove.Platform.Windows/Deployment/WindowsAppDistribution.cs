using System.ComponentModel;
using System.Runtime.InteropServices;
using PowerTrove.Application.Configuration;

namespace PowerTrove.Platform.Windows.Deployment;

/// <summary>Determines package identity without treating an unexpected Win32 failure as unpackaged.</summary>
public static class WindowsAppDistribution
{
    private static readonly Lazy<DesktopDistribution> Distribution = new(Detect);
    public static DesktopDistribution Current => Distribution.Value;

    private static DesktopDistribution Detect()
    {
        uint length = 0;
        int result = GetCurrentPackageFullName(ref length, IntPtr.Zero);
        bool packaged = result switch
        {
            122 => true, // ERROR_INSUFFICIENT_BUFFER: an identity exists.
            15700 => false, // APPMODEL_ERROR_NO_PACKAGE
            _ => throw new Win32Exception(result)
        };
        return DesktopDistribution.Create(packaged,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            // Packs/settings stay out of automatic app-data cloud backup.
            packaged ? global::Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path : null);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint length, IntPtr fullName);
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PowerTrove.Core;

namespace PowerTrove.Platform.Windows.Capture;

[SupportedOSPlatform("windows")]
public sealed class WindowsShellInterop : IShellInterop
{
    public bool TryGetSelectedFiles(nint hwnd, out IReadOnlyList<SelectedFile> files, out string? error)
    {
        files = Array.Empty<SelectedFile>();
        error = null;

        if (!OperatingSystem.IsWindows())
        {
            error = Resources.Strings.ExplorerUnsupportedView;
            return false;
        }

        try
        {
            Type? shellAppType = Type.GetTypeFromProgID("Shell.Application");
            if (shellAppType == null)
            {
                error = Resources.Strings.ExplorerError;
                return false;
            }

            dynamic? shell = Activator.CreateInstance(shellAppType);
            if (shell == null)
            {
                error = Resources.Strings.ExplorerError;
                return false;
            }

            dynamic? windows = shell.Windows();
            if (windows == null)
            {
                error = Resources.Strings.ExplorerError;
                return false;
            }

            int count = windows.Count;
            // Explorer tabs in one window share its HWND, so the window alone does not say which tab is
            // shown. Each tab has its own shell browser; the visible tab window identifies the active one.
            var candidates = new List<dynamic>();
            for (int i = 0; i < count; i++)
            {
                dynamic? window = windows.Item(i);
                if (window != null)
                {
                    try
                    {
                        if ((nint)window.HWND == hwnd)
                            candidates.Add(window);
                    }
                    catch
                    {
                        // Some windows might throw on access, safely ignore and proceed
                    }
                }
            }
            dynamic? targetWindow = null;
            if (candidates.Count == 1)
                targetWindow = candidates[0];
            else if (candidates.Count > 1)
            {
                var tabs = new nint[candidates.Count];
                for (int i = 0; i < tabs.Length; i++)
                    tabs[i] = TabWindow((object)candidates[i]);
                int chosen = ExplorerTabs.Choose(tabs, ExplorerTabs.ActiveTab(hwnd));
                targetWindow = candidates[chosen];
            }

            if (targetWindow == null)
            {
                error = Resources.Strings.ExplorerError;
                return false;
            }

            dynamic? document = targetWindow.Document;
            if (document == null)
            {
                error = Resources.Strings.ExplorerError;
                return false;
            }

            dynamic? selectedItems = document.SelectedItems();
            if (selectedItems == null)
            {
                error = Resources.Strings.ExplorerError;
                return false;
            }

            var result = new List<SelectedFile>();
            int itemsCount = selectedItems.Count;

            for (int i = 0; i < itemsCount; i++)
            {
                dynamic? item = selectedItems.Item(i);
                if (item != null)
                {
                    string? path = item.Path;
                    if (!string.IsNullOrEmpty(path))
                    {
                        bool isAvailableLocally = false;
                        long? size = null;
                        bool isAccessible = false;
                        bool isDirectory = false;
                        string? mediaType = null;
                        string name = Path.GetFileName(path);
                        string ext = Path.GetExtension(path);

                        try
                        {
                            if (File.Exists(path))
                            {
                                var fileInfo = new FileInfo(path);
                                size = fileInfo.Length;
                                isAvailableLocally = true;
                                isAccessible = true;
                            }
                            else if (Directory.Exists(path))
                            {
                                isAvailableLocally = true;
                                isAccessible = true;
                                isDirectory = true;
                                mediaType = "directory";
                            }
                        }
                        catch
                        {
                            isAccessible = false;
                        }

                        result.Add(new SelectedFile(
                            Path: path,
                            Name: name,
                            Extension: ext,
                            MediaType: mediaType,
                            SizeBytes: size,
                            IsAvailableLocally: isAvailableLocally,
                            IsAccessible: isAccessible,
                            Metadata: null,
                            IsDirectory: isDirectory
                        ));
                    }
                }
            }

            files = result;
            return true;
        }
        catch (Exception ex)
        {
            error = $"An error occurred during Shell interaction: {ex.Message}";
            return false;
        }
    }

    /// <summary>The window of one tab's shell browser, or zero when Explorer does not expose it.</summary>
    private static nint TabWindow(object window)
    {
        try
        {
            if (window is not IServiceProviderNative provider) return 0;
            Guid service = TopLevelBrowserService, browserId = typeof(IShellBrowserWindow).GUID;
            if (provider.QueryService(ref service, ref browserId, out object? browser) < 0 || browser is not IShellBrowserWindow shell)
                return 0;
            return shell.GetWindow(out nint tab) < 0 ? 0 : tab;
        }
        catch (COMException) { return 0; }
        catch (InvalidCastException) { return 0; }
    }

    private static readonly Guid TopLevelBrowserService = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProviderNative
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? value);
    }

    /// <summary>IShellBrowser; only its first IOleWindow method is used.</summary>
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowserWindow
    {
        [PreserveSig]
        int GetWindow(out nint window);
    }
}

/// <summary>Finds the shown tab of a File Explorer window with tabs (Windows 11).</summary>
[SupportedOSPlatform("windows")]
public static class ExplorerTabs
{
    /// <summary>
    /// The shell window to read: the one whose tab is the active tab, else the first one, which is all
    /// Explorer windows without tabs have.
    /// </summary>
    public static int Choose(IReadOnlyList<nint> tabWindows, nint activeTab)
    {
        ArgumentNullException.ThrowIfNull(tabWindows);
        if (tabWindows.Count == 0) throw new ArgumentException("No Explorer window.", nameof(tabWindows));
        if (activeTab != 0)
            for (int i = 0; i < tabWindows.Count; i++)
                if (tabWindows[i] == activeTab) return i;
        return 0;
    }

    /// <summary>
    /// The shown tab of an Explorer window: Explorer keeps it at the top of its tab windows' z-order, which is
    /// the first one found. Must be compared with tab windows read on an STA thread; Explorer refuses that
    /// query from an MTA thread.
    /// </summary>
    public static nint ActiveTab(nint explorerWindow)
    {
        nint tab = 0;
        while ((tab = FindWindowEx(explorerWindow, tab, "ShellTabWindowClass", null)) != 0)
            if (IsWindowVisible(tab)) return tab;
        return 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
}

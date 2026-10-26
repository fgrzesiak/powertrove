using System;
using System.Collections.Generic;
using System.IO;
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
            dynamic? targetWindow = null;

            for (int i = 0; i < count; i++)
            {
                dynamic? window = windows.Item(i);
                if (window != null)
                {
                    try
                    {
                        if ((nint)window.HWND == hwnd)
                        {
                            targetWindow = window;
                            break;
                        }
                    }
                    catch
                    {
                        // Some windows might throw on access, safely ignore and proceed
                    }
                }
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
}

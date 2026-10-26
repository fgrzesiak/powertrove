using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Threading;

namespace ContextBar.Platform.Windows.Shell
{
    public class ShellNavigator : IShellNavigator
    {
        private readonly IShellSelectionInterop _interop;
        public ShellNavigator() : this(new WindowsShellSelectionInterop()) { }
        public ShellNavigator(IShellSelectionInterop interop) => _interop = interop ?? throw new ArgumentNullException(nameof(interop));

        public Task<ShellNavigationResult> OpenFolderAndSelect(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (paths == null || paths.Count == 0)
                return Task.FromResult(new ShellNavigationResult(ShellNavigationError.InvalidSelection));
            if (!_interop.IsSupported)
                return Task.FromResult(new ShellNavigationResult(ShellNavigationError.UnsupportedPlatform));
            try
            {
                var grouped = PathGrouper.GroupPathsByFolder(paths);
                var error = grouped.Count == 0 ? ShellNavigationError.InvalidSelection : ShellNavigationError.None;
                foreach (var group in grouped)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = OpenFolderAndSelectInternal(group.Key, group.Value, cancellationToken);
                    if (!result.IsSuccess) error = result.Error;
                }
                return Task.FromResult(new ShellNavigationResult(error));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or ExternalException or
                DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
            {
                // A technical/native exception must never expose selected paths to UI or logs.
                return Task.FromResult(new ShellNavigationResult(ShellNavigationError.OpenFailed));
            }
        }

        private ShellNavigationResult OpenFolderAndSelectInternal(string folder, IReadOnlyList<string> files, CancellationToken cancellationToken)
        {
            IntPtr pidlFolder = _interop.CreatePidl(folder);
            if (pidlFolder == IntPtr.Zero)
                return new(ShellNavigationError.PidlCreationFailed);
            var error = ShellNavigationError.None;
            try
            {
                for (int i = 0; i < files.Count; i += 50)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var items = new List<IntPtr>(50);
                    try
                    {
                        foreach (var file in files.Skip(i).Take(50))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            IntPtr pointer = _interop.CreatePidl(file);
                            if (pointer == IntPtr.Zero) error = ShellNavigationError.PidlCreationFailed;
                            else items.Add(pointer);
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        if (items.Count > 0)
                        {
                            var result = ShellNavigationResult.FromHResult(_interop.OpenFolderAndSelect(pidlFolder, items));
                            if (!result.IsSuccess) error = result.Error;
                        }
                    }
                    finally
                    {
                        foreach (var pointer in items) _interop.FreePidl(pointer);
                    }
                }
            }
            finally { _interop.FreePidl(pidlFolder); }
            return new(error);
        }

        private sealed class WindowsShellSelectionInterop : IShellSelectionInterop
        {
            public bool IsSupported => OperatingSystem.IsWindows();
            // ILCreateFromPathW returns NULL on failure, not an HRESULT.
            public IntPtr CreatePidl(string path) => ILCreateFromPathW(path);
            public int OpenFolderAndSelect(IntPtr folder, IReadOnlyList<IntPtr> items) =>
                SHOpenFolderAndSelectItems(folder, (uint)items.Count, items.ToArray(), 0);
            public void FreePidl(IntPtr pointer) => ILFree(pointer);

            [DllImport("shell32.dll", ExactSpelling = true)]
            private static extern void ILFree(IntPtr pidlList);
            [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
            private static extern IntPtr ILCreateFromPathW(string pszPath);
            [DllImport("shell32.dll", ExactSpelling = true)]
            private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[] apidl, uint dwFlags);
        }
    }

    public static class PathGrouper
    {
        public static Dictionary<string, List<string>> GroupPathsByFolder(IReadOnlyList<string> paths)
        {
            var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                string folder = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(folder))
                {
                    folder = path;
                }

                if (!groups.TryGetValue(folder, out var list))
                {
                    list = new List<string>();
                    groups[folder] = list;
                }
                list.Add(path);
            }
            return groups;
        }
    }
}

using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;

namespace ContextBar.Platform.Windows.Shell
{
    public interface IShellNavigator
    {
        Task<ShellNavigationResult> OpenFolderAndSelect(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
    }
}

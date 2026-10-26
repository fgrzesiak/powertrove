using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using ContextBar.Application.ViewModels;
using ContextBar.Platform.Windows.Shell;

namespace ContextBar.Desktop
{
    public class OutputRevealer : IOutputRevealer
    {
        private readonly IShellNavigator _shellNavigator;

        public OutputRevealer(IShellNavigator shellNavigator)
        {
            _shellNavigator = shellNavigator;
        }

        public async Task RevealOutputsAsync(IReadOnlyList<string> paths)
        {
            var result = await _shellNavigator.OpenFolderAndSelect(paths);
            if (!result.IsSuccess) throw new OutputRevealException();
        }

        public Task OpenSingleOutputAsync(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch
            {
                // Ignore if it fails to open
            }
            return Task.CompletedTask;
        }
    }
}

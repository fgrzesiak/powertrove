using System.Threading.Tasks;

namespace ContextBar.Application.Services;

public interface IClipboardWriter
{
    Task WriteTextAsync(string text);
}

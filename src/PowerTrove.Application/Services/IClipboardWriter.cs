using System.Threading.Tasks;

namespace PowerTrove.Application.Services;

public interface IClipboardWriter
{
    Task WriteTextAsync(string text);
}

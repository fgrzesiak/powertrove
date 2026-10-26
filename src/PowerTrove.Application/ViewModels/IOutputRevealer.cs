using System.Collections.Generic;
using System.Threading.Tasks;

namespace PowerTrove.Application.ViewModels;

public interface IOutputRevealer
{
    Task RevealOutputsAsync(IReadOnlyList<string> paths);
    Task OpenSingleOutputAsync(string path);
}

/// <summary>A typed, content-free Shell failure; the view model supplies localized UI text.</summary>
public sealed class OutputRevealException : System.Exception
{
    public OutputRevealException() : base("Output reveal failed.") { }
}

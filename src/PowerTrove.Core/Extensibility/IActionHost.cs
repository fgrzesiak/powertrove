using System;
using System.Threading;
using System.Threading.Tasks;

namespace PowerTrove.Core.Extensibility;

/// <summary>
/// Defines the contract for an isolated action host process that executes
/// typed messages (requests) from the main UI process.
/// This is a prototype for out-of-process plugin execution.
/// </summary>
public interface IActionHost : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Checks if the host is running and healthy.
    /// </summary>
    bool IsHealthy { get; }

    /// <summary>
    /// Starts the isolated host process.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sends an execution request to the isolated host and awaits the receipt.
    /// </summary>
    Task<ActionReceipt> ExecuteActionAsync(
        string actionId,
        ContextSnapshot context,
        ActionParameters parameters,
        IProgress<ActionProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sends a preview request to the isolated host and awaits the preview result.
    /// </summary>
    Task<ActionPreview> PreviewActionAsync(
        string actionId,
        ContextSnapshot context,
        ActionParameters parameters,
        CancellationToken cancellationToken);
}

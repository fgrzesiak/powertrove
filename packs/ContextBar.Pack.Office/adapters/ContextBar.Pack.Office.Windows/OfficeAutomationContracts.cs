using ContextBar.Abstractions.Office;

namespace ContextBar.Pack.Office.Windows;

/// <summary>Created, configured, converted and disposed only on the dedicated STA thread.</summary>
public interface IOfficeAutomationSession : IDisposable
{
    void Convert(string sourcePath, string destinationPath, bool pdfToWord, CancellationToken cancellationToken);
    /// <summary>Thread-safe; touches only an owned process handle, never a COM proxy.</summary>
    void Abort();
}

public interface IOfficeAutomationFactory
{
    bool IsAvailable(OfficeApplication application);
    IOfficeAutomationSession Create(OfficeApplication application);
}

/// <summary>The lease restores the exact value, kind and original absence on Dispose.</summary>
public interface IWordReflowPreferences
{
    IDisposable SuppressConversionWarning();
}

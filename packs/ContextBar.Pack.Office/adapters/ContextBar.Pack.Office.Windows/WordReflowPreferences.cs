using ContextBar.Abstractions.Office;
using Microsoft.Win32;
using System.Security.Principal;

namespace ContextBar.Pack.Office.Windows;

public sealed record RegistryPreference(bool Exists, object? Value, RegistryValueKind Kind);

public interface IWordPreferenceStore
{
    IDisposable Enter();
    RegistryPreference Read();
    void Write(object value, RegistryValueKind kind);
    void Delete();
}

/// <summary>Only one value is temporarily changed, with its original type and absence preserved.</summary>
public sealed class WordReflowPreferences : IWordReflowPreferences
{
    private readonly IWordPreferenceStore _store;
    public WordReflowPreferences(IWordPreferenceStore? store = null) => _store = store ?? new WordPreferenceStore();
    public IDisposable SuppressConversionWarning()
    {
        var exclusive = _store.Enter();
        try
        {
            RegistryPreference old = _store.Read();
            var lease = new Lease(_store, old, exclusive);
            try { _store.Write(1, RegistryValueKind.DWord); return lease; }
            catch { lease.Dispose(); throw; }
        }
        catch { exclusive.Dispose(); throw; }
    }
    private sealed class Lease : IDisposable
    {
        private readonly IWordPreferenceStore _store;
        private readonly RegistryPreference _old;
        private readonly IDisposable _exclusive;
        private int _disposed;
        public Lease(IWordPreferenceStore store, RegistryPreference old, IDisposable exclusive) { _store = store; _old = old; _exclusive = exclusive; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { if (_old.Exists) _store.Write(_old.Value!, _old.Kind); else _store.Delete(); }
            catch (Exception) { throw new OfficeConversionException(OfficeFailure.PreferenceRestoreFailed); }
            finally { _exclusive.Dispose(); }
        }
    }
}

public sealed class WordPreferenceStore : IWordPreferenceStore
{
    private const string Key = @"Software\Microsoft\Office\16.0\Word\Options";
    private const string Name = "DisableConvertPdfWarning";
    public IDisposable Enter()
    {
        using var user = WindowsIdentity.GetCurrent();
        string identity = user.User?.Value ?? throw new OfficeConversionException(OfficeFailure.Busy);
        var semaphore = new Semaphore(1, 1, @"Global\ContextBar.WordReflow." + identity);
        if (!semaphore.WaitOne(0)) { semaphore.Dispose(); throw new OfficeConversionException(OfficeFailure.Busy); }
        return new Exclusive(semaphore);
    }
    private sealed class Exclusive : IDisposable
    {
        private Semaphore? _semaphore;
        public Exclusive(Semaphore semaphore) => _semaphore = semaphore;
        public void Dispose() { var s = Interlocked.Exchange(ref _semaphore, null); if (s is not null) { s.Release(); s.Dispose(); } }
    }
    public RegistryPreference Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key, false);
        if (key is null || !key.GetValueNames().Contains(Name, StringComparer.OrdinalIgnoreCase)) return new(false, null, RegistryValueKind.None);
        return new(true, key.GetValue(Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), key.GetValueKind(Name));
    }
    public void Write(object value, RegistryValueKind kind)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key, true);
        key.SetValue(Name, value, kind);
    }
    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key, true);
        key?.DeleteValue(Name, false);
    }
}

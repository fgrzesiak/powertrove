using System;
using System.Collections.Generic;
using PowerTrove.Abstractions.Files;

namespace PowerTrove.Abstractions.Packs;

/// <summary>Explicit type-keyed capability map; owns no engine lifetime and performs no IO.</summary>
public sealed class PackHostServices : IPackHostServices
{
    private readonly Dictionary<Type, object> _services = new();
    public PackHostServices(Version hostVersion, FileActionServices files)
    {
        HostVersion = hostVersion ?? throw new ArgumentNullException(nameof(hostVersion));
        Files = files ?? throw new ArgumentNullException(nameof(files));
    }
    public Version HostVersion { get; }
    public FileActionServices Files { get; }
    public T? GetService<T>() where T : class => _services.TryGetValue(typeof(T), out var value) ? (T)value : null;
    public PackHostServices Add<T>(T service) where T : class
    {
        ArgumentNullException.ThrowIfNull(service);
        _services.Add(typeof(T), service);
        return this;
    }
}

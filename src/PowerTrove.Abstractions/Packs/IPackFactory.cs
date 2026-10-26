using System;
using PowerTrove.Abstractions.Files;

namespace PowerTrove.Abstractions.Packs;

/// <summary>Typed, explicitly supplied host capabilities. Implementations never discover services globally.</summary>
public interface IPackHostServices
{
    Version HostVersion { get; }
    FileActionServices Files { get; }
    T? GetService<T>() where T : class;
}

public interface IPackFactory
{
    ActionPackInfo Info { get; }
    IActionPack Create(IPackHostServices host);
}

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class ActionPackFactoryAttribute : Attribute
{
    public ActionPackFactoryAttribute(Type factoryType) => FactoryType = factoryType ?? throw new ArgumentNullException(nameof(factoryType));
    public Type FactoryType { get; }
}

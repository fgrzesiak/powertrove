using System.Runtime.InteropServices;
using PowerTrove.Abstractions.Manifest;

namespace PowerTrove.Core.Catalog;

public static class PackCompatibility
{
    public static bool IsSupported(ActionManifest manifest, Version host, string? os = null, string? architecture = null,
        bool requireWorkerInput = false)
    {
        os ??= OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "macos";
        architecture ??= RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        return manifest.ProtocolVersion == ActionPackManifest.ProtocolVersion &&
            Version.TryParse(manifest.MinHostVersion, out var minimum) && minimum <= host &&
            Supports(manifest.PlatformConstraints?.Os, os) && Supports(manifest.PlatformConstraints?.MinimumArchitecture, architecture) &&
            // No unspecified SDK host service is assumed to exist.
            (manifest.Capabilities is null || manifest.Capabilities.All(c => c is "ocr" or "office.input")) &&
            (!requireWorkerInput || manifest.Actions.All(a => a.InputKinds.Count == 1 && a.InputKinds[0] is "files" or "table"));
    }
    private static bool Supports(IReadOnlyList<string>? allowed, string value) =>
        allowed is null || allowed.Contains(value, StringComparer.OrdinalIgnoreCase);
}

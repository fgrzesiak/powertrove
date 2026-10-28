namespace PowerTrove.PackHost;

/// <summary>Opt-in startup-only codes, never exception text, paths, stack traces or content.</summary>
internal static class WorkerStartupDiagnostics
{
    public static string Describe(string stage, Exception error)
    {
        stage = stage is "arguments" or "parent" or "workRoot" or "loadPack" or "session" ? stage : "unknown";
        string category = error switch
        {
            UnauthorizedAccessException => "accessDenied",
            InvalidDataException => "invalidData",
            FileNotFoundException => "dependencyMissing",
            FileLoadException => "assemblyLoad",
            BadImageFormatException => "badImage",
            DllNotFoundException => "nativeDependencyMissing",
            TypeLoadException => "typeLoad",
            MissingMethodException => "missingMethod",
            System.Runtime.InteropServices.COMException => "interop",
            IOException => "io",
            _ => "internal"
        };
        string reason = error is InvalidDataException ? error.Message switch
        {
            "Pack payload or host version is unsupported." => "unsupportedPayload",
            "Installed manifest changed after authorization." => "manifestIdentity",
            "Pack payload length mismatch." => "payloadLength",
            "Pack payload changed during verification." => "payloadChanged",
            "Pack payload hash mismatch." => "payloadHash",
            "Pack payload inventory mismatch." => "payloadInventory",
            "Pack factory does not match its manifest." => "factoryIdentity",
            _ => "unspecified"
        } : "unspecified";
        return $"Worker startup: stage={stage}; category={category}; reason={reason}; hresult=0x{error.HResult:X8}";
    }
}

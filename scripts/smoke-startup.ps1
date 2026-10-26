#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Executable)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Startup validation requires Windows.' }
$executablePath = (Resolve-Path -LiteralPath $Executable).Path
if (Get-Process -Name PowerTrove -ErrorAction SilentlyContinue) {
    throw 'Close existing PowerTrove instances before the startup check.'
}
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PowerTroveIconProbe {
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint ExtractIconEx(string file, int index, IntPtr large, IntPtr small, uint count);
}
'@
if ([PowerTroveIconProbe]::ExtractIconEx($executablePath, -1, [IntPtr]::Zero, [IntPtr]::Zero, 0) -eq 0) {
    throw 'Published executable has no embedded Windows icon.'
}
$process = Start-Process -FilePath $executablePath -WorkingDirectory ([IO.Path]::GetDirectoryName($executablePath)) -PassThru
try {
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    $ready = $false
    while ($deadline.Elapsed.TotalSeconds -lt 30) {
        $process.Refresh()
        if ($process.HasExited) { throw 'Published application exited before showing its startup window.' }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero -and $process.Responding) { $ready = $true; break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $ready) { throw 'Published application did not show a responsive startup window within 30 seconds.' }
    Start-Sleep -Seconds 3
    $process.Refresh()
    if ($process.HasExited -or -not $process.Responding -or $process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'Published application failed after its initial window was created.'
    }
    Write-Host 'Startup check passed: published application has a responsive window and embedded icon.'
}
finally {
    # Only the process created by this check is stopped. Never terminate an existing user instance.
    if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $process.Dispose()
}

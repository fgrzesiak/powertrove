<#
.SYNOPSIS
Builds and starts the PowerTrove WinUI desktop app (unpackaged, framework-dependent).

.DESCRIPTION
Checks the .NET SDK and the Windows App Runtime that matches the Microsoft.WindowsAppSDK
package referenced by src/PowerTrove.Desktop, builds the desktop project and starts the EXE.
A WinUI WinExe does not print startup exceptions to the terminal, so if the app exits right
away, recent Application event log errors are shown.

.EXAMPLE
scripts\run-desktop.ps1
scripts\run-desktop.ps1 -Configuration Release
scripts\run-desktop.ps1 -Profile Core -UseInstalledPacks
scripts\run-desktop.ps1 -NoBuild -Wait
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    # Local builds include current pack sources; Core exercises downloaded packs.
    [ValidateSet('Core', 'Base', 'Full')]
    [string]$Profile = 'Full',
    # Exercise downloaded pack overrides instead of preferring local pack sources.
    [switch]$UseInstalledPacks,
    # Start the last build without building again.
    [switch]$NoBuild,
    # Keep the terminal attached until the app exits and return its exit code.
    [switch]$Wait
)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)
$project = 'src\PowerTrove.Desktop\PowerTrove.Desktop.csproj'
$worker = 'src\PowerTrove.PackHost\PowerTrove.PackHost.csproj'

if (Get-Process -Name PowerTrove -ErrorAction SilentlyContinue) {
    throw 'PowerTrove is already running. Exit it from the system tray before starting a local build.'
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK not found. Install the .NET 10 SDK: https://dotnet.microsoft.com/download'
}

# The framework-dependent build needs the matching Windows App Runtime on this machine.
[xml]$projectXml = Get-Content $project
$sdk = $projectXml.Project.ItemGroup.PackageReference |
    Where-Object { $_.Include -eq 'Microsoft.WindowsAppSDK' } | Select-Object -First 1
if ($sdk) {
    $required = [version]$sdk.Version
    $family = if ($required.Major -ge 2) { "Microsoft.WindowsAppRuntime.$($required.Major)" }
              else { "Microsoft.WindowsAppRuntime.$($required.Major).$($required.Minor)" }
    $arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'Arm64' } else { 'X64' }
    $runtime = Get-AppxPackage -Name $family | Where-Object {
        $_.Architecture -eq $arch -and ($required.Major -lt 2 -or [version]$_.Version -ge $required)
    }
    if (-not $runtime) {
        throw ("Windows App Runtime $required ($arch) is not installed. Install it from " +
               'https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads')
    }
    Write-Host "Windows App Runtime: $family $(($runtime | Sort-Object Version -Descending)[0].Version) ($arch)"
}

if (-not $NoBuild) {
    dotnet build $project -c $Configuration -p:PowerTroveProfile=$Profile --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE)." }
}

$targetPath = dotnet msbuild $project -nologo -p:Configuration=$Configuration -p:PowerTroveProfile=$Profile -getProperty:TargetPath
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the desktop output path.' }
$exe = Get-Item ([IO.Path]::ChangeExtension($targetPath.Trim(), '.exe')) -ErrorAction SilentlyContinue
if (-not $exe) { throw "PowerTrove.exe not found for $Configuration. Run without -NoBuild." }

if (-not $NoBuild) {
    # The app always starts downloaded packs through the worker next to its EXE.
    # Build with the same configuration and put the worker and its dependencies there.
    dotnet build $worker -c $Configuration --nologo -v minimal -o $exe.DirectoryName
    if ($LASTEXITCODE -ne 0) { throw "Worker build failed (exit code $LASTEXITCODE)." }
}
if (-not (Test-Path (Join-Path $exe.DirectoryName 'PowerTrove.PackHost.exe'))) {
    throw 'PowerTrove.PackHost.exe is missing. Run again without -NoBuild.'
}

$started = Get-Date
$startInfo = [Diagnostics.ProcessStartInfo]::new($exe.FullName)
$startInfo.WorkingDirectory = $exe.DirectoryName
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.Environment['POWERTROVE_PREFER_BUILTIN_PACKS'] = if ($UseInstalledPacks -or $Profile -eq 'Core') { '0' } else { '1' }
$process = [Diagnostics.Process]::Start($startInfo)
Write-Host "Started PowerTrove (PID $($process.Id)): $($exe.FullName)"

Start-Sleep -Seconds 3
if ($process.HasExited) {
    Write-Warning "PowerTrove exited immediately (exit code $($process.ExitCode))."
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Level = 2; StartTime = $started } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'PowerTrove' } | Select-Object -First 3 |
        ForEach-Object { Write-Host "--- $($_.TimeCreated) $($_.ProviderName)"; Write-Host $_.Message }
    exit 1
}

if ($Wait) {
    $process.WaitForExit()
    exit $process.ExitCode
}

<#
.SYNOPSIS
Builds and starts the PowerTrove WinUI desktop app (unpackaged, framework-dependent).

.DESCRIPTION
Checks the .NET SDK and the Windows App Runtime that matches the Microsoft.WindowsAppSDK
package referenced by src/PowerTrove.Desktop, builds the desktop project and starts the EXE.
A WinUI WinExe does not print startup exceptions to the terminal, so if the app exits right
away, recent Application event log errors are shown.

It also prepares the browser bridge for testing: it builds the browser host next to the EXE,
points the powertrove:// link at this build, checks that the unpacked extension from
extensions\chromium is loaded in Chrome or Edge and says when it needs loading or reloading.
Browsers neither install an extension nor open their extensions page from the command line.
The script then stays open; Enter or Ctrl+C stops PowerTrove and restores the previous
browser registrations. A session that was not cleaned up is restored on the next start.

.EXAMPLE
scripts\run-desktop.ps1
scripts\run-desktop.ps1 -Configuration Release
scripts\run-desktop.ps1 -Profile Core -UseInstalledPacks
scripts\run-desktop.ps1 -NoBuild -Wait
scripts\run-desktop.ps1 -NoBrowser
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
    [switch]$Wait,
    # Start without browser bridge setup and return right away.
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)
$project = 'src\PowerTrove.Desktop\PowerTrove.Desktop.csproj'
$worker = 'src\PowerTrove.PackHost\PowerTrove.PackHost.csproj'
$browserHost = 'src\PowerTrove.BrowserHost\PowerTrove.BrowserHost.csproj'

# Everything the browser test setup changes. The native-messaging keys and manifest are shared
# with an installed PowerTrove, so they are backed up and restored instead of only deleted.
$browserBackup = Join-Path $env:TEMP 'powertrove-dev-browser'
$browserKeys = @(
    'HKCU\Software\Classes\powertrove',
    'HKCU\Software\PowerTrove\BrowserHost',
    'HKCU\Software\Google\Chrome\NativeMessagingHosts\com.powertrove.browser',
    'HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.powertrove.browser')
$browserManifest = Join-Path $env:LOCALAPPDATA 'PowerTrove\BrowserHost\com.powertrove.browser.json'

function Invoke-Reg([string]$arguments) {
    # reg.exe reports success on stderr; Windows PowerShell 5.1 would turn that into an error.
    cmd.exe /d /c "reg.exe $arguments >nul 2>&1"
    return $LASTEXITCODE -eq 0
}

function Save-BrowserState([string]$owner) {
    New-Item $browserBackup -ItemType Directory -Force | Out-Null
    for ($i = 0; $i -lt $browserKeys.Count; $i++) {
        $file = Join-Path $browserBackup "key$i.reg"
        if ((Invoke-Reg "query `"$($browserKeys[$i])`"") -and -not (Invoke-Reg "export `"$($browserKeys[$i])`" `"$file`" /y")) {
            throw "Could not back up $($browserKeys[$i])."
        }
    }
    if (Test-Path -LiteralPath $browserManifest) {
        Copy-Item -LiteralPath $browserManifest (Join-Path $browserBackup 'manifest.json')
    }
    Set-Content (Join-Path $browserBackup 'owner.txt') $owner
}

function Restore-BrowserState {
    $ownerFile = Join-Path $browserBackup 'owner.txt'
    if (-not (Test-Path $ownerFile)) { return }
    # Stop the dev build and its browser host, including instances the powertrove:// link started.
    $directory = Split-Path (Get-Content $ownerFile -TotalCount 1)
    Get-Process -Name PowerTrove, PowerTrove.BrowserHost -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($directory, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object {
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
            Wait-Process -Id $_.Id -Timeout 10 -ErrorAction SilentlyContinue
        }
    for ($i = 0; $i -lt $browserKeys.Count; $i++) {
        [void](Invoke-Reg "delete `"$($browserKeys[$i])`" /f")
        $file = Join-Path $browserBackup "key$i.reg"
        if ((Test-Path $file) -and -not (Invoke-Reg "import `"$file`"")) {
            throw "Could not restore $($browserKeys[$i]); the backup stays in $browserBackup."
        }
    }
    Remove-Item -LiteralPath $browserManifest -Force -ErrorAction SilentlyContinue
    $manifestBackup = Join-Path $browserBackup 'manifest.json'
    if (Test-Path $manifestBackup) { Copy-Item $manifestBackup $browserManifest -Force }
    Remove-Item -LiteralPath $browserBackup -Recurse -Force
    Write-Host 'Browser registrations restored.'
}

# Chromium derives an unpacked extension's ID from its folder path: SHA-256 of the UTF-16 path,
# first 16 bytes as hex digits mapped to a-p.
function Get-UnpackedExtensionId([string]$path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = $sha.ComputeHash([Text.Encoding]::Unicode.GetBytes($path)) } finally { $sha.Dispose() }
    $hex = -join ($hash[0..15] | ForEach-Object { $_.ToString('x2') })
    -join ($hex.ToCharArray() | ForEach-Object { [char](97 + [Convert]::ToInt32([string]$_, 16)) })
}

# Browsers only load an extension the user adds, so this checks and guides instead of installing.
function Initialize-BrowserExtension {
    $folder = (Resolve-Path 'extensions\chromium').Path
    $fingerprint = -join (Get-ChildItem $folder -Recurse -File | Sort-Object FullName |
        ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash })
    $fingerprintFile = Join-Path $env:TEMP 'powertrove-dev-extension.sha256'
    $changed = -not (Test-Path $fingerprintFile) -or (Get-Content $fingerprintFile -Raw).Trim() -ne $fingerprint
    $pattern = '"path":"(' + [regex]::Escape($folder.Replace('\', '\\')) + ')"'
    $browsers = @(
        @{ Name = 'Chrome'; Data = "$env:LOCALAPPDATA\Google\Chrome\User Data"; Page = 'chrome://extensions' },
        @{ Name = 'Edge'; Data = "$env:LOCALAPPDATA\Microsoft\Edge\User Data"; Page = 'edge://extensions' })
    $loaded = $false
    foreach ($browser in $browsers) {
        if (-not (Test-Path $browser.Data)) { continue }
        $match = $null
        foreach ($profileDir in Get-ChildItem $browser.Data -Directory) {
            foreach ($name in 'Secure Preferences', 'Preferences') {
                $file = Join-Path $profileDir.FullName $name
                if (-not $match -and (Test-Path $file)) {
                    $found = [regex]::Match([IO.File]::ReadAllText($file), $pattern, 'IgnoreCase')
                    if ($found.Success) { $match = $found.Groups[1].Value.Replace('\\', '\') }
                }
            }
        }
        if (-not $match) { continue }
        $loaded = $true
        $id = Get-UnpackedExtensionId $match
        Write-Host "$($browser.Name): PowerTrove extension loaded (ID $id)."
        if ($changed) {
            Write-Warning "Extension files changed: open $($browser.Page) and click Reload on PowerTrove."
        }
    }
    if (-not $loaded) {
        Write-Host 'The PowerTrove extension is not loaded in Chrome or Edge. Open chrome://extensions,'
        Write-Host "turn on Developer mode, choose Load unpacked and select $folder"
        Write-Host "Expected extension ID: $(Get-UnpackedExtensionId $folder)"
    }
    elseif ($changed) {
        # A browser keeps running the old files until Reload. Remember the new state only after
        # confirmation, so stopping here repeats the reminder on the next start.
        [void](Read-Host 'Press Enter after reloading the extension (or Ctrl+C to stop)')
    }
    Set-Content $fingerprintFile $fingerprint
}

if (-not $NoBrowser -and (Test-Path (Join-Path $browserBackup 'owner.txt'))) {
    Write-Host 'Restoring browser registrations from a session that was not cleaned up.'
    Restore-BrowserState
}

$runningProcesses = @(Get-Process -Name PowerTrove -ErrorAction SilentlyContinue)
foreach ($runningProcess in $runningProcesses) {
    Write-Host "Stopping running PowerTrove instance (PID $($runningProcess.Id))."
    Stop-Process -Id $runningProcess.Id -Force -ErrorAction Stop
    Wait-Process -Id $runningProcess.Id -Timeout 10 -ErrorAction SilentlyContinue
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
    if (-not $NoBrowser) {
        # The app pairs a browser only when PowerTrove.BrowserHost.exe sits next to it, as in the package.
        dotnet build $browserHost -c $Configuration --nologo -v minimal -o $exe.DirectoryName
        if ($LASTEXITCODE -ne 0) { throw "Browser host build failed (exit code $LASTEXITCODE)." }
    }
}
if (-not (Test-Path (Join-Path $exe.DirectoryName 'PowerTrove.PackHost.exe'))) {
    throw 'PowerTrove.PackHost.exe is missing. Run again without -NoBuild.'
}
if (-not $NoBrowser -and -not (Test-Path (Join-Path $exe.DirectoryName 'PowerTrove.BrowserHost.exe'))) {
    throw 'PowerTrove.BrowserHost.exe is missing. Run again without -NoBuild.'
}

try {
    if (-not $NoBrowser) {
        Save-BrowserState $exe.FullName
        # The installer registers this link for installed builds; here it starts the dev build.
        $protocol = 'HKCU:\Software\Classes\powertrove'
        New-Item "$protocol\shell\open\command" -Force | Out-Null
        Set-Item $protocol -Value 'URL:PowerTrove Browser Connection'
        New-ItemProperty $protocol -Name 'URL Protocol' -Value '' -Force | Out-Null
        Set-Item "$protocol\shell\open\command" -Value "`"$($exe.FullName)`" --browser-connect `"%1`""
        Initialize-BrowserExtension
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
    if (-not $NoBrowser) {
        # Quitting PowerTrove does not end the session: a powertrove:// link may start it again.
        Write-Host 'Browser test setup is active. Press Enter or Ctrl+C to stop PowerTrove and clean up.'
        [void](Read-Host)
    }
}
finally {
    if (-not $NoBrowser) { Restore-BrowserState }
}

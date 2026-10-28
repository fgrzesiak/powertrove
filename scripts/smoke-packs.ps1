#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$AssetDirectory,
    [Parameter(Mandatory=$true)][string]$WorkerExecutable,
    [Parameter(Mandatory=$true)][string]$CatalogSha256
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Pack startup validation requires Windows.' }
$assetRoot = [IO.Path]::GetFullPath($AssetDirectory)
$worker = [IO.Path]::GetFullPath($WorkerExecutable)
$hostVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($worker).FileVersion
if (-not [version]::TryParse($hostVersion, [ref]([version]$null))) { throw 'Worker version is invalid.' }
$catalogPath = Join-Path $assetRoot 'catalog.json'
if ($CatalogSha256 -cnotmatch '^[a-f0-9]{64}$' -or
    (Get-FileHash -LiteralPath $catalogPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $CatalogSha256) {
    throw 'Use the exact catalog already verified by the release command.'
}
$catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
if ($catalog.schemaVersion -cne '2' -or @($catalog.packs).Count -gt 998) { throw 'Unexpected catalog.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
# The caller verifies catalog/bundle signatures, lengths and every payload hash first.
# The worker independently pins each extracted manifest and checks its payload inventory.
foreach ($entry in $catalog.packs) {
    $manifest = $entry.manifest
    if ($manifest.id -cnotmatch '^[a-z0-9]+([.-][a-z0-9]+)*$' -or
        $manifest.version -cnotmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Unsafe pack identity.' }
    # The worker rejects noncanonical paths. $env:TEMP can be an 8.3 short path on hosted runners,
    # which .NET expands, so derive both directories from the long known-folder path.
    $localAppData = [Environment]::GetFolderPath('LocalApplicationData')
    $unpack = Join-Path (Join-Path $localAppData 'Temp') ('PowerTrove-PackSmoke-' + [guid]::NewGuid().ToString('N'))
    $work = Join-Path (Join-Path $localAppData 'PowerTrove\workers') ([guid]::NewGuid().ToString('N'))
    $process = $null
    try {
        Write-Output ('Checking worker startup: ' + $manifest.id + '@' + $manifest.version)
        [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $assetRoot ($manifest.id + '-' + $manifest.version + '.cbpack')), $unpack)
        $digest = (Get-FileHash -LiteralPath (Join-Path $unpack 'manifest.json') -Algorithm SHA256).Hash
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = $worker
        $start.Arguments = '--pack-dir "' + $unpack + '" --manifest-sha256 ' + $digest + ' --parent-pid ' + $PID + ' --work-root "' + $work + '"'
        $start.WorkingDirectory = [IO.Path]::GetDirectoryName($worker)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardInput = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.EnvironmentVariables['POWERTROVE_WORKER_STARTUP_DIAGNOSTICS'] = '1'
        # The protocol is raw UTF-8 lines. A UTF-8 console code page (hosted runners) otherwise
        # makes the redirected stdin writer emit a BOM before the hello message.
        $stdinEncoding = New-Object Text.UTF8Encoding($false)
        if ($start.PSObject.Properties['StandardInputEncoding']) {
            $start.StandardInputEncoding = $stdinEncoding
            $process = [Diagnostics.Process]::Start($start)
        }
        else {
            # Windows PowerShell 5.1 takes the stdin encoding from the console.
            $consoleEncoding = [Console]::InputEncoding
            try {
                [Console]::InputEncoding = $stdinEncoding
                $process = [Diagnostics.Process]::Start($start)
            }
            finally { [Console]::InputEncoding = $consoleEncoding }
        }
        $stderr = $process.StandardError.ReadToEndAsync()
        $hello = @{ type='hello'; protocolVersion=1; hostVersion=$hostVersion } | ConvertTo-Json -Compress
        $process.StandardInput.WriteLine($hello)
        $line = $process.StandardOutput.ReadLineAsync()
        if (-not $line.Wait(20000) -or -not $line.Result) {
            $status = if ($process.HasExited) { 'exit code ' + $process.ExitCode } else { 'startup timeout' }
            if ($process.HasExited -and $stderr.Wait(1000)) {
                foreach ($diagnostic in @($stderr.Result -split '\r?\n')) {
                    if ($diagnostic -cmatch '^Worker startup: stage=[A-Za-z]+; category=[A-Za-z]+; reason=[A-Za-z]+; hresult=0x[0-9A-F]{8}$') {
                        Write-Output $diagnostic
                    }
                }
            }
            throw ('Signed pack worker did not become ready: ' + $status + '.')
        }
        try { $ready = $line.Result | ConvertFrom-Json }
        catch { throw 'Signed pack worker returned a malformed handshake.' }
        if ($ready.type -cne 'ready' -or @($ready.packs).Count -ne 1 -or
            $ready.packs[0].id -cne $manifest.id -or $ready.packs[0].version -cne $manifest.version) {
            throw 'Signed pack worker identity mismatch.'
        }
        $process.StandardInput.WriteLine('{"type":"shutdown"}')
        if (-not $process.WaitForExit(10000) -or $process.ExitCode -ne 0) { throw 'Signed pack worker did not exit cleanly.' }
        Write-Output ('Verified worker startup: ' + $manifest.id + '@' + $manifest.version)
    }
    finally {
        if ($null -ne $process) {
            if (-not $process.HasExited) { $process.Kill(); [void]$process.WaitForExit(5000) }
            $process.Dispose()
        }
        foreach ($owned in @($unpack, $work)) {
            if (Test-Path -LiteralPath $owned) { Remove-Item -LiteralPath $owned -Recurse -Force }
        }
    }
}

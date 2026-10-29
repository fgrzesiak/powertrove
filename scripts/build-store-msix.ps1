#requires -Version 5.1
<#
.SYNOPSIS
Builds an unsigned, self-contained Core MSIX in a fresh temporary directory.
.DESCRIPTION
Requires a clean checkout and Windows SDK MakeAppx/MakePri. Does not install,
trust a certificate, contact Partner Center or publish. Package identity is explicit.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Identity,
    [Parameter(Mandatory=$true)][string]$Publisher,
    [Parameter(Mandatory=$true)][string]$PackageVersion,
    [Parameter(Mandatory=$true)][string]$HostVersion,
    [string]$CoreZip,
    [string]$CoreZipSha256,
    [string]$ReleaseVersion,
    [string]$OutputDirectory = (Join-Path $env:TEMP ('PowerTrove-MSIX-' + [Guid]::NewGuid().ToString('N'))),
    [string]$SdkDirectory = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'MSIX packaging requires Windows.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$temporaryRoots = @($env:TEMP, $env:RUNNER_TEMP) | Where-Object { $_ } | ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\') + '\' }
if (-not ($temporaryRoots | Where-Object { $output.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }) -or
    $output.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Candidate output must be a fresh directory under TEMP or RUNNER_TEMP, outside the source tree.'
}
if (Test-Path -LiteralPath $output) { throw 'Output exists. Choose a fresh temporary directory.' }
foreach ($tool in @('makeappx.exe', 'makepri.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $SdkDirectory $tool))) { throw 'The selected Windows SDK is incomplete.' }
}
Push-Location $root
try {
    $status = & git status --porcelain
    if ($LASTEXITCODE -ne 0 -or $status) { throw 'Commit the reviewed source before building an exact candidate.' }
    $revision = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $revision -cnotmatch '^[a-f0-9]{40}$') { throw 'Cannot identify exact public source.' }
    # Prevent build-server proliferation in constrained environments. Child publishes inherit this.
    $previousNodeReuse = $env:MSBUILDDISABLENODEREUSE
    $env:MSBUILDDISABLENODEREUSE = '1'
    if ($CoreZip) {
        if ($CoreZipSha256 -cnotmatch '^[a-f0-9]{64}$' -or -not $ReleaseVersion) { throw 'An existing Core ZIP needs its exact hash and release version.' }
        # This branch must not publish Desktop or PackHost again. The existing
        # solution build has already built ReleaseTools; only its executable runs.
        [IO.Directory]::CreateDirectory($output) | Out-Null
    }
    else {
        if ($CoreZipSha256 -or $ReleaseVersion) { throw 'Supply CoreZip with its hash/release version, or use the standalone host build.' }
        & dotnet run --project src/PowerTrove.ReleaseTools -c Release -- package --repo-root $root --output-dir $output --version $HostVersion
        if ($LASTEXITCODE -ne 0) { throw 'Core payload publication failed.' }
        $CoreZip = Join-Path $output "PowerTrove-v$HostVersion-win-x64.zip"
        $CoreZipSha256 = (Get-FileHash -LiteralPath $CoreZip -Algorithm SHA256).Hash.ToLowerInvariant()
        $ReleaseVersion = $HostVersion
    }
    $payload = Join-Path $output 'payload'
    $inputEvidence = Join-Path $output 'input-payload.json'
    & dotnet run --project src/PowerTrove.ReleaseTools -c Release --no-build -- extract-msix-payload --core-zip $CoreZip --core-sha256 $CoreZipSha256 --payload $payload --source-revision $revision --version $HostVersion --release-version $ReleaseVersion --input-evidence $inputEvidence
    if ($LASTEXITCODE -ne 0) { throw 'Existing Core ZIP provenance or extraction failed.' }
    & dotnet run --project src/PowerTrove.ReleaseTools -c Release --no-build -- prepare-msix --repo-root $root --payload $payload --identity $Identity --publisher $Publisher --package-version $PackageVersion
    if ($LASTEXITCODE -ne 0) { throw 'MSIX payload/identity validation failed.' }

    # Derive package logos from the existing owned Windows icon; no new binary source assets.
    Add-Type -AssemblyName System.Drawing
    $assets = Join-Path $payload 'StoreAssets'
    [IO.Directory]::CreateDirectory($assets) | Out-Null
    $icon = New-Object System.Drawing.Icon((Join-Path $root 'src/PowerTrove.Desktop/Assets/PowerTrove.ico'), 256, 256)
    $source = $icon.ToBitmap()
    try {
        foreach ($entry in @(@('StoreLogo.png',50), @('Square44x44Logo.png',44), @('Square150x150Logo.png',150))) {
            $bitmap = New-Object System.Drawing.Bitmap([int]$entry[1], [int]$entry[1])
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.DrawImage($source, 0, 0, [int]$entry[1], [int]$entry[1])
                $bitmap.Save((Join-Path $assets $entry[0]), [Drawing.Imaging.ImageFormat]::Png)
            }
            finally { $graphics.Dispose(); $bitmap.Dispose() }
        }
    }
    finally { $source.Dispose(); $icon.Dispose() }

    $config = Join-Path $output 'priconfig.xml'
    # Index only our new resources. Scanning the full self-contained payload would
    # re-import the supplier PRI files and produce conflicting WinUI resource keys.
    $resourceRoot = Join-Path $output 'package-resources'
    [IO.Directory]::CreateDirectory($resourceRoot) | Out-Null
    Copy-Item -LiteralPath (Join-Path $payload 'Strings') -Destination $resourceRoot -Recurse
    Copy-Item -LiteralPath $assets -Destination $resourceRoot -Recurse
    function Invoke-StoreSdk([string]$Tool, [string[]]$Arguments, [string]$LogName) {
        $log = Join-Path $output $LogName
        & (Join-Path $SdkDirectory $Tool) @Arguments *> $log
        if ($LASTEXITCODE -ne 0) { throw "Windows SDK $Tool failed; inspect the temporary $LogName log." }
    }
    Invoke-StoreSdk 'makepri.exe' @('createconfig', '/cf', $config, '/dq', 'en-US') 'makepri-config.log'
    # This is one bilingual package, not a bundle of language resource packages.
    # The SDK default splits German into a PRI that would need separate packaging.
    [xml]$resourceConfiguration = Get-Content -LiteralPath $config -Raw
    $resourceConfiguration.resources.packaging.RemoveAll()
    $resourceConfiguration.Save($config)
    Invoke-StoreSdk 'makepri.exe' @('new', '/pr', $resourceRoot, '/cf', $config, '/mn', (Join-Path $payload 'AppxManifest.xml'), '/of', (Join-Path $payload 'resources.pri')) 'makepri.log'
    $resourceDump = Join-Path $output 'package-resource-dump.xml'
    Invoke-StoreSdk 'makepri.exe' @('dump', '/if', (Join-Path $payload 'resources.pri'), '/of', $resourceDump) 'makepri-dump.log'
    [xml]$compiledResources = Get-Content -LiteralPath $resourceDump -Raw
    foreach ($key in @('AppDisplayName', 'AppPublisherName', 'AppDescription')) {
        $candidates = @($compiledResources.SelectNodes("//NamedResource[@name='$key']/Candidate"))
        if ($candidates.Count -ne 2 -or
            -not ($candidates | Where-Object { $_.qualifiers -match 'Language-en-US' }) -or
            -not ($candidates | Where-Object { $_.qualifiers -match 'Language-de-DE' })) {
            throw 'Compiled package metadata must include every key in both English and German.'
        }
    }
    $package = Join-Path $output 'PowerTrove.msix'
    Invoke-StoreSdk 'makeappx.exe' @('pack', '/d', $payload, '/p', $package, '/h', 'SHA256') 'makeappx.log'
    $proofFile = Join-Path $output 'payload-proof.json'
    & dotnet run --project src/PowerTrove.ReleaseTools -c Release --no-build -- verify-msix-payload --msix $package --payload $payload --input-evidence $inputEvidence --proof-file $proofFile
    if ($LASTEXITCODE -ne 0) { throw 'MSIX archive verification or Core payload preservation failed.' }
    $inventory = @(Get-Content -LiteralPath $proofFile -Raw | ConvertFrom-Json | ForEach-Object {
        @{ path = $_.Path; bytes = $_.Bytes; sha256 = $_.Sha256 }
    })
    @{ schemaVersion = 2; sourceRevision = $revision; identity = $Identity; publisher = $Publisher;
       packageVersion = $PackageVersion; hostVersion = $HostVersion; architecture = 'x64'; profile = 'Core';
       releaseVersion = $ReleaseVersion; sourceZipSha256 = $CoreZipSha256; payloadVerified = $true; sdkValidation = 'Passed';
       packageSha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant();
       signature = 'Unsigned'; windowsAcceptance = 'Not run'; certification = 'Not submitted';
       files = $inventory } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'msix-candidate.json') -Encoding UTF8
    Write-Output "Unsigned local MSIX prepared: $package"
    Write-Output 'Install and test only in a disposable Windows environment. This command does not grant Store approval.'
}
finally {
    if (Test-Path variable:previousNodeReuse) { $env:MSBUILDDISABLENODEREUSE = $previousNodeReuse }
    Pop-Location
}

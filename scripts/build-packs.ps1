#requires -Version 7.0
<#
.SYNOPSIS
Publishes supported first-party file packs and creates signed owner-channel bundles.
.DESCRIPTION
Requires Windows, .NET 10 SDK, and a PEM P-256 private key outside the repository.
DryRun never writes signatures or installable .cbpack files.
The Tables composition is intentionally excluded: worker protocol v1 rejects table inputs.
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/pack-channel'),
    [string]$SigningKeyPath = $env:POWERTROVE_PACK_SIGNING_KEY_FILE,
    [switch]$DryRun,
    [string[]]$Packs = @('Files', 'Images', 'Pdf', 'Media', 'Data', 'Office')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Pack publishing requires Windows for native adapter composition.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $DryRun -and [string]::IsNullOrWhiteSpace($SigningKeyPath)) {
    throw 'SigningKeyPath or POWERTROVE_PACK_SIGNING_KEY_FILE is required.'
}
$allowed = @('Files', 'Images', 'Pdf', 'Media', 'Data', 'Office')
foreach ($pack in $Packs) {
    if ($pack -cnotin $allowed) { throw "Unsupported or unsafe worker-v1 pack: $pack" }
}
if (($Packs | Select-Object -Unique).Count -ne $Packs.Count) { throw 'Duplicate pack name.' }
if (Test-Path (Join-Path $output 'packs')) { throw 'Output/packs already exists. Use a fresh output directory.' }
$publishRoot = Join-Path $output ('.publish-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
try {
    foreach ($pack in $Packs) {
        $project = Join-Path $root "packs/PowerTrove.Pack.$pack/composition/PowerTrove.Pack.$pack.Composition/PowerTrove.Pack.$pack.Composition.csproj"
        $destination = Join-Path $publishRoot "PowerTrove.Pack.$pack"
        & dotnet publish $project -c Release -r win-x64 --self-contained false -o $destination
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $pack." }
    }
    $generator = Join-Path $root 'src/PowerTrove.ReleaseTools/PowerTrove.ReleaseTools.csproj'
    $arguments = @('run', '--project', $generator, '--', 'pack-bundles',
        '--published-root', $publishRoot, '--output-dir', $output)
    if ($DryRun) { $arguments += '--dry-run' }
    else { $arguments += @('--key-file', $SigningKeyPath) }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Pack bundle generator failed.' }
}
finally {
    if (Test-Path $publishRoot) { Remove-Item -LiteralPath $publishRoot -Recurse -Force }
}

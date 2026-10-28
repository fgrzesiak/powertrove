#requires -Version 7.0
<#
.SYNOPSIS
Builds a self-contained Base or Full Windows payload with a separate PackHost worker.
.DESCRIPTION
Base receives an app-local metadata catalog from published first-party file packs.
This command does not sign, install, upload or enable production downloads.
#>
[CmdletBinding()]
param(
    [ValidateSet('Core','Base','Full')][string]$Profile = 'Core',
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Profile distribution requires Windows.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $output) { throw 'Output directory already exists. Use a fresh directory.' }
New-Item -ItemType Directory -Path $output | Out-Null
try {
    $desktopProject = Join-Path $root 'src/PowerTrove.Desktop/PowerTrove.Desktop.csproj'
    & dotnet publish $desktopProject -c Release -r win-x64 --self-contained true "-p:PowerTroveProfile=$Profile" -o $output
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }

    $hostProject = Join-Path $root 'src/PowerTrove.PackHost/PowerTrove.PackHost.csproj'
    # The worker shares the app's self-contained runtime: publish it separately, then add only its own files.
    $hostOutput = "$output.worker"
    if (Test-Path $hostOutput) { throw 'Worker output directory already exists. Use a fresh directory.' }
    try {
        & dotnet publish $hostProject -c Release -r win-x64 --self-contained true -o $hostOutput
        if ($LASTEXITCODE -ne 0) { throw 'PackHost publish failed.' }
        foreach ($file in Get-ChildItem -LiteralPath $hostOutput -Recurse -File) {
            $relative = [IO.Path]::GetRelativePath($hostOutput, $file.FullName)
            $target = Join-Path $output $relative
            if (Test-Path -LiteralPath $target) {
                if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
                    throw "Worker and app publish different versions of $relative."
                }
                continue
            }
            New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $target
        }
    }
    finally { if (Test-Path $hostOutput) { Remove-Item -LiteralPath $hostOutput -Recurse -Force } }
    if (-not (Test-Path (Join-Path $output 'PowerTrove.PackHost.exe'))) {
        throw 'Expected PackHost executable is missing.'
    }

    if ($Profile -eq 'Base') {
        # The checked-in catalog.json is a placeholder. Replace it only with
        # real factory metadata generated from published compositions.
        $catalogRoot = Join-Path $env:TEMP ('PowerTrove-Catalog-' + [guid]::NewGuid().ToString('N'))
        try {
            & pwsh -NoProfile -File (Join-Path $root 'scripts/build-packs.ps1') -DryRun -OutputDirectory $catalogRoot
            if ($LASTEXITCODE -ne 0) { throw 'Pack metadata generation failed.' }
            $catalog = Join-Path $catalogRoot 'packs/catalog.json'
            if (-not (Test-Path $catalog)) { throw 'Generated discovery catalog is missing.' }
            Copy-Item -LiteralPath $catalog -Destination (Join-Path $output 'catalog.json') -Force
        }
        finally {
            if (Test-Path $catalogRoot) { Remove-Item -LiteralPath $catalogRoot -Recurse -Force }
        }
    }
}
catch {
    # The directory is owned by this invocation and never contains user output.
    if (Test-Path $output) { Remove-Item -LiteralPath $output -Recurse -Force }
    throw
}

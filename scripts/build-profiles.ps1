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
    [ValidateSet('Base','Full')][string]$Profile = 'Base',
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
    $desktopProject = Join-Path $root 'src/ContextBar.Desktop/ContextBar.Desktop.csproj'
    & dotnet publish $desktopProject -c Release -r win-x64 --self-contained true "-p:ContextBarProfile=$Profile" -o $output
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }

    $hostProject = Join-Path $root 'src/ContextBar.PackHost/ContextBar.PackHost.csproj'
    $hostOutput = Join-Path $output 'packhost'
    & dotnet publish $hostProject -c Release -r win-x64 --self-contained true -o $hostOutput
    if ($LASTEXITCODE -ne 0) { throw 'PackHost publish failed.' }
    if (-not (Test-Path (Join-Path $hostOutput 'ContextBar.PackHost.exe'))) {
        throw 'Expected PackHost executable is missing.'
    }

    # Keep the original app-root launch path functional until the Desktop host
    # prefers the isolated packhost directory (CB-167).
    foreach ($name in @('ContextBar.PackHost.exe', 'ContextBar.PackHost.deps.json', 'ContextBar.PackHost.runtimeconfig.json')) {
        $source = Join-Path $hostOutput $name
        $target = Join-Path $output $name
        if (-not (Test-Path $source) -or (Test-Path $target)) { throw "Worker compatibility asset conflict: $name" }
        Copy-Item -LiteralPath $source -Destination $target
    }

    if ($Profile -eq 'Base') {
        # The checked-in catalog.json is a placeholder. Replace it only with
        # real factory metadata generated from published compositions.
        $catalogRoot = Join-Path $env:TEMP ('ContextBar-Catalog-' + [guid]::NewGuid().ToString('N'))
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

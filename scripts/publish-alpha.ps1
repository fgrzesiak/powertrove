#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [Parameter(Mandatory)][string]$Evidence,
    [Parameter(Mandatory)][string]$Installer,
    [switch]$AllowUnsignedAlpha
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Public-release verification requires Windows.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$candidate = [IO.Path]::GetFullPath($CandidateDirectory)
$evidencePath = [IO.Path]::GetFullPath($Evidence)
$installerPath = [IO.Path]::GetFullPath($Installer)
$identity = Get-Content -LiteralPath (Join-Path $candidate 'candidate.json') -Raw | ConvertFrom-Json
if ($identity.repository -cne 'fgrzesiak/powertrove' -or $identity.version -cnotmatch '^\d+\.\d+\.\d+$' -or
    $identity.tag -cnotmatch ('^v' + [regex]::Escape($identity.version) + '-alpha\.[1-9][0-9]{0,5}$') -or
    $identity.revision -cnotmatch '^[a-f0-9]{40}$') { throw 'Candidate identity is invalid.' }
$zip = Join-Path $candidate "PowerTrove-v$($identity.version)-win-x64.zip"
$report = Join-Path $candidate ('approval-' + [guid]::NewGuid().ToString('N'))
Push-Location $root
try {
    $head = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -cne $identity.revision) { throw 'Use the exact public checkout recorded by the candidate.' }
    $status = & git status --porcelain
    if ($LASTEXITCODE -ne 0 -or $status) { throw 'Release verification requires a clean public checkout.' }
    # The evidence must match the final bytes. Unsigned alpha is an explicit policy, not a passing signature.
    $signingArguments = @()
    if ($AllowUnsignedAlpha) { $signingArguments = @('--allow-unsigned-alpha') }
    & dotnet run --project src/PowerTrove.ReleaseTools -- verify-candidate --zip $zip --evidence $evidencePath --report-dir $report --installer $installerPath --public @signingArguments
    if ($LASTEXITCODE -ne 0) { throw 'Public release gate did not pass. Nothing was uploaded.' }
    $gate = Get-Content -LiteralPath (Join-Path $report 'release-readiness.json') -Raw | ConvertFrom-Json
    if ($gate.Revision -cne $identity.revision -or $gate.Version -cne $identity.version -or
        $gate.ReleaseVersion -cne $identity.tag.Substring(1) -or -not $gate.PublicReady -or
        $gate.UnsignedAlpha -ne [bool]$AllowUnsignedAlpha) {
        throw 'Candidate identity does not match the public release gate.'
    }
    $notes = Join-Path $candidate 'release-notes.md'
    if (-not (Test-Path -LiteralPath $notes) -or (Get-Item -LiteralPath $notes).Length -eq 0) {
        throw 'Write candidate-specific release-notes.md with supported workflows and known limitations first.'
    }
    if ($AllowUnsignedAlpha) {
        $originalNotes = Get-Content -LiteralPath $notes -Raw
        $notes = Join-Path $report 'release-notes.md'
        "> **Unsigned experimental alpha.** Publisher identity is not verified by a code-signing certificate.`n`n$originalNotes" |
            Set-Content -LiteralPath $notes -Encoding utf8NoBOM
    }
    & gh release create $identity.tag $zip (Join-Path $candidate 'SHA256SUMS') $installerPath `
        --repo 'fgrzesiak/powertrove' --target $identity.revision --title "PowerTrove $($identity.tag)" `
        --notes-file $notes --prerelease --draft
    if ($LASTEXITCODE -ne 0) { throw 'Draft creation failed. Inspect GitHub before retrying; no release is published automatically.' }
    Write-Host 'Draft created. Review its assets and notes in GitHub, then publish it manually.'
}
finally { Pop-Location }

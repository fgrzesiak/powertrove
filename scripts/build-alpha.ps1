#requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The alpha candidate must be built on Windows.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $root
try {
    $release = Get-Content -LiteralPath (Join-Path $root 'release.json') -Raw | ConvertFrom-Json
    if ($release.version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
        $release.channel -cne 'alpha' -or "$($release.sequence)" -cnotmatch '^[1-9][0-9]{0,5}$') {
        throw 'Invalid alpha release identity.'
    }
    $revision = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $revision -cnotmatch '^[a-f0-9]{40}$') { throw 'Cannot identify public source revision.' }
    $status = & git status --porcelain
    if ($LASTEXITCODE -ne 0 -or $status) { throw 'Build requires a clean public checkout.' }
    & dotnet build PowerTrove.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Windows build failed.' }
    & dotnet test PowerTrove.slnx -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Automated tests failed.' }
    $output = Join-Path $root 'artifacts/alpha'
    & dotnet run --project src/PowerTrove.ReleaseTools -- package --repo-root $root --output-dir $output --version $release.version --alpha-sequence $release.sequence
    if ($LASTEXITCODE -ne 0) { throw 'Candidate packaging failed.' }
    @{ version = $release.version; tag = "v$($release.version)-alpha.$($release.sequence)";
       repository = 'fgrzesiak/powertrove'; revision = $revision; target = 'win-x64';
       publicReady = $false; windowsAcceptance = 'Not run'; signing = 'Blocked' } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'candidate.json') -Encoding utf8NoBOM
    Write-Host 'Candidate built. Complete native acceptance, notices, signatures and owner approval before publication.'
}
finally { Pop-Location }

#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('All', 'Build', 'Test', 'Package', 'Installer')]
    [string]$Phase = 'All',
    [string]$Iscc,
    [string]$OutputDirectory = $env:POWERTROVE_ALPHA_OUTPUT
)
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
    $output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) }
        else { Join-Path $env:TEMP ("PowerTrove-Alpha-$($release.version)-$($release.sequence)-$revision") }
    $packDirectory = Join-Path $output 'packs'
    $packPlan = Join-Path $packDirectory 'pack-plan.json'
    if (-not (Test-Path -LiteralPath $packPlan) -and $Phase -in @('All','Build','Package')) {
        if (-not (Test-Path -LiteralPath $output)) { New-Item -ItemType Directory -Path $output | Out-Null }
        & node (Join-Path $root 'scripts/plan-packs.mjs') --output-dir $packDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Pack version planning failed. Nothing was packaged.' }
    }
    if (-not (Test-Path -LiteralPath $packPlan)) { throw 'Build/plan the exact candidate before testing or compiling its installer.' }
    $packProperty = '-p:PowerTrovePackVersionsFile=' + (Join-Path $packDirectory 'pack-versions.props')
    if ($Phase -in @('All', 'Installer') -and
        (-not $Iscc -or -not (Test-Path -LiteralPath $Iscc -PathType Leaf))) {
        throw 'Select an Inno Setup compiler with -Iscc (see scripts/setup-inno.ps1).'
    }
    if ($Phase -in @('All', 'Build')) {
        Write-Host 'Building the Windows solution.'
        & dotnet build PowerTrove.slnx -c Release $packProperty
        if ($LASTEXITCODE -ne 0) { throw 'Windows build failed.' }
    }
    if ($Phase -in @('All', 'Test')) {
        $results = Join-Path $output 'test-results'
        Write-Host 'Testing with a three-minute hang limit; reports are written alongside the candidate.'
        # Browser/COM integration suites share Windows resources; run projects sequentially.
        & dotnet test PowerTrove.slnx -c Release --no-build --no-restore '-m:1' `
            --blame-hang --blame-hang-timeout 3m --blame-hang-dump-type none `
            --logger trx --logger 'console;verbosity=normal' --results-directory $results
        if ($LASTEXITCODE -ne 0) { throw 'Automated tests failed or timed out. Inspect candidate/test-results.' }
    }
    if ($Phase -in @('All', 'Package')) {
        & dotnet run --project src/PowerTrove.ReleaseTools -- package --repo-root $root --output-dir $output --version $release.version --alpha-sequence $release.sequence --pack-plan-file $packPlan
        if ($LASTEXITCODE -ne 0) { throw 'Candidate packaging failed.' }
        @{ version = $release.version; tag = "v$($release.version)-alpha.$($release.sequence)";
           repository = 'fgrzesiak/powertrove'; revision = $revision; target = 'win-x64';
           publicReady = $false; windowsAcceptance = 'Not run'; signing = 'Unsigned';
           packPlanSha256 = (Get-FileHash -LiteralPath $packPlan -Algorithm SHA256).Hash.ToLowerInvariant() } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'candidate.json') -Encoding utf8NoBOM
    }
    if ($Phase -in @('All', 'Installer')) {
        & dotnet run --project src/PowerTrove.ReleaseTools -- installer-build --repo-root $root --output-dir $output --version $release.version --iscc $Iscc
        if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    }
    if ($Phase -notin @('All', 'Package', 'Installer')) { return }
    Write-Host 'Candidate built. Complete native acceptance, notices, signatures and owner approval before publication.'
}
finally { Pop-Location }

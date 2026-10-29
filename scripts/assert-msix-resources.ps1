# Verifies the compiled package index, not just the presence of PRI files.
function Assert-MsixResources {
    param([Parameter(Mandatory)][string]$SourceDump, [Parameter(Mandatory)][string]$PackageDump,
        [Parameter(Mandatory)][string]$Identity)
    [xml]$sourceIndex = Get-Content -LiteralPath $SourceDump -Raw
    [xml]$packageIndex = Get-Content -LiteralPath $PackageDump -Raw
    $sourceResources = @($sourceIndex.SelectNodes('//NamedResource'))
    if (-not $sourceResources.Count) { throw 'The original application resource index is empty.' }
    foreach ($resource in $sourceResources) {
        $uri = $resource.GetAttribute('uri') -replace '^ms-resource://[^/]+/', "ms-resource://$Identity/"
        $matches = @($packageIndex.SelectNodes('//NamedResource') | Where-Object { $_.GetAttribute('uri') -ceq $uri })
        if ($matches.Count -ne 1 -or $matches[0].InnerXml -cne $resource.InnerXml) {
            throw 'A compiled application resource or its candidate set was lost during MSIX merging.'
        }
    }
    foreach ($resource in @('Files/App.xbf', 'Files/MainWindow.xbf', 'Files/SettingsWindow.xbf',
        'Files/Microsoft.UI.Xaml/Themes/themeresources.xbf')) {
        if (-not ($packageIndex.SelectNodes('//NamedResource') | Where-Object {
            $_.GetAttribute('uri') -ceq "ms-resource://$Identity/$resource" -and $_.SelectSingleNode('Candidate') })) {
            throw 'The package resource index is missing an application or WinUI startup resource.'
        }
    }
}

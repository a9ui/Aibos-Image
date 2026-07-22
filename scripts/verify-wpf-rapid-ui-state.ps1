param(
    [string]$Configuration = 'Release',
    [string]$OutputPath = (Join-Path $env:TEMP ('photoviewer-wpf-rapid-ui-state-' + [guid]::NewGuid().ToString('N') + '.json'))
)

$ErrorActionPreference = 'Stop'
if ($OutputPath.Contains('"')) { throw 'OutputPath cannot contain a double quote.' }

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net8.0-windows\PhotoViewer.Wpf.exe"

dotnet build $project -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Remove-Item -LiteralPath $OutputPath -ErrorAction SilentlyContinue
$process = Start-Process -FilePath $exe `
    -ArgumentList @('--rapid-ui-state-smoke', ('"{0}"' -f $OutputPath)) `
    -WindowStyle Hidden -Wait -PassThru

if (-not (Test-Path -LiteralPath $OutputPath)) {
    throw "WPF rapid UI/state process exited without producing $OutputPath"
}

$result = Get-Content -Raw -LiteralPath $OutputPath | ConvertFrom-Json
$failures = @()
if ($process.ExitCode -ne 0) { $failures += "process exit code $($process.ExitCode)" }
if ($result.ok -ne $true) { $failures += 'result.ok was false' }
if ($result.fixtureCount -ne 321 -or $result.selectionChurnAccepted -ne $true -or $result.preview.stableLatestSelection -ne $true) {
    $failures += 'catalog, selection churn, or latest preview decode contract failed'
}
if ($result.modeAndLayoutChurn -ne $true -or $result.resizePreviewDidNotPersist -ne $true -or $result.resizeCommitted -ne $true) {
    $failures += 'Grid/List, display/aspect, or right-panel commit boundary failed'
}
if ($result.allFavoriteSemanticsCount -ne 321 -or $result.favoriteLevelsAssigned -ne $true `
    -or $result.unseenCount -ne $result.unseenFilteredCount -or $result.dotsHidden -ne $true -or $result.dotsShown -ne $true) {
    $failures += 'Favorite Lv1-5/All or Unseen filter/dot semantics failed under rapid switching'
}
if ($result.tabChurn -ne $true -or $result.finalTabs.Count -ne 3 -or $result.reloadPinned -ne $true) {
    $failures += 'preview tab open/reorder/close/pin state failed'
}
if ($result.discardedSearches -ne 5 -or $result.finalSearchApplied -ne $true) {
    $failures += 'stale search result was not discarded or final query was not applied'
}
if ($result.finalStatePersisted -ne $true -or $result.restored -ne $true) {
    $failures += 'only-final-state persistence or reload restoration failed'
}
if ($result.heartbeatCount -lt 5) { $failures += 'UI heartbeat did not advance' }
if ($result.enhancementPassive -ne $true -or $result.sourceUntouched -ne $true -or $result.isolated -ne $true) {
    $failures += 'enhancement, source, or temp-only isolation contract failed'
}

$result | ConvertTo-Json -Depth 10
if ($failures.Count -gt 0) {
    throw ('WPF rapid UI/state gate failed: ' + ($failures -join '; '))
}

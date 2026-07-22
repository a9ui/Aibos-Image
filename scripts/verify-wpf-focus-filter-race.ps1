param(
    [string]$Configuration = 'Release',
    [string]$OutputPath = (Join-Path $env:TEMP ('photoviewer-wpf-focus-filter-race-' + [guid]::NewGuid().ToString('N') + '.json'))
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
    -ArgumentList @('--focus-filter-race-smoke', ('"{0}"' -f $OutputPath)) `
    -WindowStyle Hidden -Wait -PassThru

if (-not (Test-Path -LiteralPath $OutputPath)) {
    throw "WPF focus/filter race process exited without producing $OutputPath"
}

$result = Get-Content -Raw -LiteralPath $OutputPath | ConvertFrom-Json
$failures = @()
if ($process.ExitCode -ne 0) { $failures += "process exit code $($process.ExitCode)" }
if ($result.ok -ne $true) { $failures += 'result.ok was false' }
if ($result.catalogCount -ne 10 -or $result.iterations -ne 20) { $failures += 'fixture or iteration contract failed' }
if ($result.noResultRecovered -ne $true -or $result.pinRetained -ne $true -or $result.tabRecovered -ne $true) { $failures += 'no-results/modal/tab/pin recovery failed' }
if ($result.favoriteExact -ne $true -or $result.unseenStable -ne $true -or $result.dateStable -ne $true -or $result.folderStable -ne $true) {
    $failures += 'Favorite/Unseen/date/folder filter churn failed'
}
if ($result.modeSelectionStable -ne $true -or $result.searchLatestWins -ne $true) { $failures += 'Grid/List selection or latest-search focus failed' }
if ($result.filteredTabSafe -ne $true -or $result.filteredTabRecovered -ne $true) { $failures += 'filtered-out preview tab/modal reconciliation failed' }
if ($result.heartbeatCount -lt 20) { $failures += 'dispatcher heartbeat did not advance' }
if ($result.jsonValid -ne $true -or $result.sourceUntouched -ne $true -or $result.enhancementPassive -ne $true -or $result.isolated -ne $true) {
    $failures += 'JSON/source/enhancement/temp-only isolation failed'
}

$result | ConvertTo-Json -Depth 8
if ($failures.Count -gt 0) {
    throw ('WPF focus/filter race gate failed: ' + ($failures -join '; '))
}

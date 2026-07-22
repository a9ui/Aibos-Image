param(
    [string]$Configuration = 'Release',
    [string]$OutputPath = (Join-Path $env:TEMP ('photoviewer-wpf-shutdown-state-' + [guid]::NewGuid().ToString('N') + '.json'))
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
    -ArgumentList @('--shutdown-state-smoke', ('"{0}"' -f $OutputPath)) `
    -WindowStyle Hidden -Wait -PassThru

if (-not (Test-Path -LiteralPath $OutputPath)) {
    throw "WPF shutdown-state process exited without producing $OutputPath"
}

$result = Get-Content -Raw -LiteralPath $OutputPath | ConvertFrom-Json
$failures = @()
if ($process.ExitCode -ne 0) { $failures += "process exit code $($process.ExitCode)" }
if ($result.ok -ne $true) { $failures += 'result.ok was false' }
if ($result.setupReady -ne $true -or $result.closeMs -ge 300 -or $result.flushCount -ne 1) {
    $failures += 'rapid setup, sub-debounce close, or exactly-once flush failed'
}
if ($result.searchDiscarded -ne $true -or $result.hoverDiscarded -ne $true -or $result.previewSettled -ne $true `
    -or $result.oldAsyncDidNotMutateState -ne $true) {
    $failures += 'pending search/decode/hover work was not cancelled safely'
}
if ($result.finalPersisted -ne $true -or $result.restored -ne $true) {
    $failures += 'final query/tab/pin/layout/panel/filter/dot/delete-confirm state did not restore'
}
if ($result.closeStoreIsolation -ne $true -or $result.reloadCloseIsolation -ne $true `
    -or $result.residueFree -ne $true -or $result.enhancementPassive -ne $true) {
    $failures += 'source/shared stores, atomic residue, or enhancement isolation failed'
}
foreach ($scenario in @('malformed', 'protectedFuture', 'contended')) {
    $snapshot = $result.$scenario
    if ($snapshot.unchanged -ne $true -or $snapshot.closed -ne $true -or $snapshot.flushCount -ne 1 `
        -or $snapshot.pendingDiscarded -ne $true -or $snapshot.closeMs -ge 1000 `
        -or $snapshot.lockRemainedOwned -ne $true -or $snapshot.residueFree -ne $true) {
        $failures += "$scenario state refusal did not remain intact and non-blocking"
    }
}

$result | ConvertTo-Json -Depth 10
if ($failures.Count -gt 0) {
    throw ('WPF shutdown-state gate failed: ' + ($failures -join '; '))
}

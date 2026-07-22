param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputPath = (Join-Path $env:TEMP ('photoviewer-wpf-recent-write-ownership-' + [guid]::NewGuid().ToString('N') + '.json'))
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
    -ArgumentList @('--recent-write-ownership-smoke', ('"{0}"' -f $OutputPath)) `
    -WindowStyle Hidden -Wait -PassThru

if (-not (Test-Path -LiteralPath $OutputPath)) {
    throw "WPF recent-write ownership process exited without producing $OutputPath"
}

$result = Get-Content -Raw -LiteralPath $OutputPath | ConvertFrom-Json
$failures = @()
if ($process.ExitCode -ne 0) { $failures += "process exit code $($process.ExitCode)" }
if ($result.ok -ne $true) { $failures += 'result.ok was false' }
if ($result.firstExplicitCommit -ne $true -or $result.secondExplicitCommit -ne $true) {
    $failures += 'explicit folder-set commits did not each write once'
}
if ($result.generalStateAndRefreshByteIdentical -ne $true -or $result.externalOwnerByteIdentical -ne $true `
    -or $result.explicitCommitThenCloseByteIdentical -ne $true -or $result.retryThenCloseByteIdentical -ne $true) {
    $failures += 'general state, refresh, or close rewrote shared recent bytes'
}
if ($result.latestExternalHistoryMerged -ne $true -or $result.unknownFieldsPreserved -ne $true `
    -or $result.mergedHistoryCount -ne 12) {
    $failures += 'latest-under-lock merge, unknown-field preservation, or 12-set cap failed'
}
if ($result.failedWriteNotMarkedSuccessful -ne $true -or $result.retrySucceededAndSameSetDeduplicated -ne $true) {
    $failures += 'failed write retry or same-successful-set deduplication failed'
}
if ($result.sourceUntouched -ne $true -or $result.residueFree -ne $true) {
    $failures += 'source isolation or lock/temp cleanup failed'
}

$result | ConvertTo-Json -Depth 10
if ($failures.Count -gt 0) {
    throw ('WPF recent-write ownership gate failed: ' + ($failures -join '; '))
}

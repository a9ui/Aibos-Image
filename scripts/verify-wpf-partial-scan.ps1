$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$exe = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\bin\Release\net8.0-windows\PhotoViewer.Wpf.exe'
$result = Join-Path ([IO.Path]::GetTempPath()) ("photoviewer-wpf-partial-scan-" + [guid]::NewGuid().ToString('N') + '.json')

dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

try {
    $process = Start-Process -FilePath $exe `
        -ArgumentList @('--partial-scan-smoke', ('"{0}"' -f $result)) `
        -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) {
        $details = if (Test-Path -LiteralPath $result) { Get-Content -Raw -LiteralPath $result } else { 'no result file' }
        throw "partial scan smoke exited $($process.ExitCode): $details"
    }

    $smoke = Get-Content -Raw -LiteralPath $result | ConvertFrom-Json
    $required = @(
        'partialEnumerationPhase',
        'validImagesPublished',
        'requestedSetOwned',
        'recoverableStatus',
        'partialStateOwned',
        'partialRecentOwned',
        'oneRecentCommit',
        'canceledEnumerationPhase',
        'cancelAccepted',
        'canceledImmediateIsolation',
        'newerRunWon',
        'canceledRunNeverPersisted',
        'successfulSetsPersisted',
        'exactRecentOwnership',
        'unknownFieldsPreserved',
        'sourceUntouched',
        'unrelatedCacheUntouched',
        'missingRootsStayedMissing',
        'loadCtsBalanced',
        'isolated',
        'residueFree'
    )
    $failed = @($required | Where-Object { $smoke.$_ -ne $true })
    if ($smoke.ok -ne $true -or $failed.Count -gt 0) {
        throw "partial scan contract failed ($($failed -join ', ')): $(Get-Content -Raw -LiteralPath $result)"
    }

    $smoke | ConvertTo-Json -Depth 8
}
finally {
    Remove-Item -LiteralPath $result -Force -ErrorAction SilentlyContinue
}

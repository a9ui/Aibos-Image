$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$exe = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\bin\Release\net8.0-windows\PhotoViewer.Wpf.exe'
$result = Join-Path ([IO.Path]::GetTempPath()) ("photoviewer-wpf-scan-cancel-" + [guid]::NewGuid().ToString('N') + '.json')

dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

try {
    $process = Start-Process -FilePath $exe `
        -ArgumentList @('--scan-cancel-smoke', ('"{0}"' -f $result)) `
        -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) {
        $details = if (Test-Path -LiteralPath $result) { Get-Content -Raw -LiteralPath $result } else { 'no result file' }
        throw "scan cancel smoke exited $($process.ExitCode): $details"
    }

    $smoke = Get-Content -Raw -LiteralPath $result | ConvertFrom-Json
    $required = @(
        'initiallyInactive',
        'enumerationPhase',
        'enumerationCancelAccepted',
        'enumerationCancelUi',
        'doubleCancelNoOp',
        'enumerationDraftPreserved',
        'enumerationImmediateIsolation',
        'enumerationLateIgnored',
        'baselinePublished',
        'publicationPhase',
        'publicationCancelAccepted',
        'publicationCancelUi',
        'publicationCancelIsolation',
        'publicationLateIgnored',
        'newerRunWon',
        'unavailableSupersessionPhase',
        'unavailableIntentReturnedBeforeDelayedValid',
        'unavailableIntentOwnedUi',
        'unavailableIntentImmediateIsolation',
        'unavailableIntentLateIsolation',
        'unavailableNeverPersisted',
        'canceledFoldersNeverPublished',
        'successfulRunsPersisted',
        'unknownFieldsPreserved',
        'sourceUntouched',
        'unrelatedCacheUntouched',
        'isolated',
        'residueFree',
        'loadCtsBalanced'
    )
    $failed = @($required | Where-Object { $smoke.$_ -ne $true })
    if ($smoke.ok -ne $true -or $failed.Count -gt 0 -or $smoke.enumerationCancelMs -ge 100 -or $smoke.publicationCancelMs -ge 100) {
        throw "scan cancel contract failed ($($failed -join ', ')): $(Get-Content -Raw -LiteralPath $result)"
    }

    $smoke | ConvertTo-Json -Depth 8
}
finally {
    Remove-Item -LiteralPath $result -Force -ErrorAction SilentlyContinue
}

param(
    [string]$Configuration = "Release",
    [string]$OutputPath = (Join-Path $env:TEMP "photoviewer-wpf-external-stale-source.json")
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj"
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net8.0-windows\PhotoViewer.Wpf.exe"
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)

if (-not $fullOutputPath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "External stale-source output must stay under the temp directory."
}

dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (Test-Path -LiteralPath $fullOutputPath) {
    Remove-Item -LiteralPath $fullOutputPath -Force
}

$process = Start-Process -FilePath $exe `
    -ArgumentList @('--external-stale-source-smoke', ('"{0}"' -f $fullOutputPath)) `
    -WindowStyle Hidden `
    -PassThru `
    -Wait

if (-not (Test-Path -LiteralPath $fullOutputPath)) {
    throw "External stale-source smoke did not produce a result."
}

$result = Get-Content -Raw -LiteralPath $fullOutputPath | ConvertFrom-Json
$result | ConvertTo-Json -Depth 8
if ($process.ExitCode -ne 0) { exit $process.ExitCode }
if ($result.ok -ne $true `
    -or $result.missingSessionReconciled -ne $true `
    -or $result.modalReconciled -ne $true `
    -or $result.closedHistoryRebound -ne $true `
    -or $result.closedFocusRestored -ne $true `
    -or $result.renamedIsNewIdentity -ne $true `
    -or $result.corruptHistoryRetained -ne $true `
    -or $result.corruptPreviewRecoverable -ne $true `
    -or $result.corruptModalRecoverable -ne $true `
    -or $result.noSurvivorReconciled -ne $true `
    -or $result.storesRetained -ne $true `
    -or $result.sourcesReadOnly -ne $true) {
    exit 1
}

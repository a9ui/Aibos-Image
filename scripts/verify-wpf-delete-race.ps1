param(
    [string]$Configuration = "Release",
    [string]$OutputPath = (Join-Path $env:TEMP "photoviewer-wpf-delete-race.json")
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj"
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net8.0-windows\PhotoViewer.Wpf.exe"
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)

if (-not $fullOutputPath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Delete-race output must stay under the temp directory."
}

dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (Test-Path -LiteralPath $fullOutputPath) {
    Remove-Item -LiteralPath $fullOutputPath -Force
}

$process = Start-Process -FilePath $exe `
    -ArgumentList @('--delete-race-smoke', ('"{0}"' -f $fullOutputPath)) `
    -WindowStyle Hidden `
    -PassThru `
    -Wait

if (-not (Test-Path -LiteralPath $fullOutputPath)) {
    throw "Delete-race smoke did not produce a result."
}

$result = Get-Content -Raw -LiteralPath $fullOutputPath | ConvertFrom-Json
$result | ConvertTo-Json -Depth 8
if ($process.ExitCode -ne 0) { exit $process.ExitCode }
if ($result.ok -ne $true `
    -or $result.asyncDeleteReconciled -ne $true `
    -or $result.sparseDeleteReconciled -ne $true `
    -or $result.bulkPartialReconciled -ne $true `
    -or $result.cancelWasNonDestructive -ne $true `
    -or $result.refreshRaceReconciled -ne $true `
    -or $result.refreshDeleteDuringCatalogYield -ne $true `
    -or $result.refreshCatalogClean -ne $true `
    -or $result.refreshSelectionPreserved -ne $true `
    -or $result.refreshPreviewSafe -ne $true `
    -or $result.refreshModalSafe -ne $true `
    -or $result.refreshStateClean -ne $true `
    -or $result.sameNameRegenerationAllowed -ne $true `
    -or $result.historiesRetained -ne $true `
    -or $result.tempRecycleOnly -ne $true) {
    exit 1
}

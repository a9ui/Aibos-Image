param(
    [string]$Configuration = "Release",
    [string]$OutputPath = (Join-Path $env:TEMP "photoviewer-wpf-scan-materialization-race.json")
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj"
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net8.0-windows\PhotoViewer.Wpf.exe"
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)

if (-not $fullOutputPath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Scan materialization race output must stay under the temp directory."
}

dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (Test-Path -LiteralPath $fullOutputPath) {
    Remove-Item -LiteralPath $fullOutputPath -Force
}

$process = Start-Process -FilePath $exe `
    -ArgumentList @('--scan-materialization-race-smoke', ('"{0}"' -f $fullOutputPath)) `
    -WindowStyle Hidden `
    -PassThru `
    -Wait

if (-not (Test-Path -LiteralPath $fullOutputPath)) {
    throw "Scan materialization race smoke did not produce a result."
}

$result = Get-Content -Raw -LiteralPath $fullOutputPath | ConvertFrom-Json
$result | ConvertTo-Json -Depth 8
if ($process.ExitCode -ne 0) { exit $process.ExitCode }
if ($result.ok -ne $true `
    -or $result.vanishedSourceSkipped -ne $true `
    -or $result.recoverableWarning -ne $true `
    -or $result.validSelectionAndModal -ne $true `
    -or $result.stateReconciled -ne $true `
    -or $result.storesUnchanged -ne $true `
    -or $result.sourcesReadOnly -ne $true `
    -or $result.isolated -ne $true `
    -or $result.residueFree -ne $true `
    -or $result.fileCountMetric -ne 2) {
    exit 1
}

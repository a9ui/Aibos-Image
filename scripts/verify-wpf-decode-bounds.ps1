param(
    [string]$Configuration = "Release",
    [string]$OutputPath = (Join-Path $env:TEMP "photoviewer-wpf-decode-bounds.json")
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj"
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net10.0-windows\PhotoViewer.Wpf.exe"
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)

if (-not $fullOutputPath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Decode-bounds output must stay under the temp directory."
}

dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (Test-Path -LiteralPath $fullOutputPath) {
    Remove-Item -LiteralPath $fullOutputPath -Force
}

$process = Start-Process -FilePath $exe `
    -ArgumentList @('--decode-bounds-smoke', ('"{0}"' -f $fullOutputPath)) `
    -WindowStyle Hidden `
    -PassThru

if (-not $process.WaitForExit(60000)) {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    throw "Decode-bounds smoke timed out after 60 seconds."
}

if (-not (Test-Path -LiteralPath $fullOutputPath)) {
    throw "Decode-bounds smoke did not produce a result."
}

$result = Get-Content -Raw -LiteralPath $fullOutputPath | ConvertFrom-Json
$result | ConvertTo-Json -Depth 12
if ($process.ExitCode -ne 0) { exit $process.ExitCode }
if ($result.ok -ne $true `
    -or $result.thumbnail.thumbnailBounded -ne $true `
    -or $result.preview.previewBounded -ne $true `
    -or $result.modal.modalBounded -ne $true `
    -or $result.dispatcher.dispatcherResponsive -ne $true `
    -or $result.memory.memoryBounded -ne $true `
    -or $result.latestSelection.latestSelectionWon -ne $true `
    -or $result.fidelityPreserved -ne $true `
    -or $result.subpixelWidthFallbackBounded -ne $true `
    -or $result.nonNativeSourceBudget.oversizedBmpHeaderRejectedBeforeDecode -ne $true `
    -or $result.nonNativeSourceBudget.enforced -ne $true `
    -or $result.sourceAndJobsUntouched -ne $true) {
    exit 1
}

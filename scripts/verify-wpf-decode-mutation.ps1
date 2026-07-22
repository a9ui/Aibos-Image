param(
    [string]$Configuration = "Release",
    [string]$OutputPath = (Join-Path $env:TEMP "photoviewer-wpf-decode-mutation.json")
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj"
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net8.0-windows\PhotoViewer.Wpf.exe"
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)

if (-not $fullOutputPath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Decode-mutation output must stay under the temp directory."
}

dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (Test-Path -LiteralPath $fullOutputPath) {
    Remove-Item -LiteralPath $fullOutputPath -Force
}

$process = Start-Process -FilePath $exe `
    -ArgumentList @('--decode-mutation-smoke', ('"{0}"' -f $fullOutputPath)) `
    -WindowStyle Hidden `
    -PassThru

if (-not $process.WaitForExit(90000)) {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    throw "Decode-mutation smoke timed out after 90 seconds."
}

if (-not (Test-Path -LiteralPath $fullOutputPath)) {
    throw "Decode-mutation smoke did not produce a result."
}

$result = Get-Content -Raw -LiteralPath $fullOutputPath | ConvertFrom-Json
$result | ConvertTo-Json -Depth 8
if ($process.ExitCode -ne 0) { exit $process.ExitCode }
if ($result.ok -ne $true `
    -or $result.corruptFailureClearedStaleBitmap -ne $true `
    -or $result.corruptRecovery -ne $true `
    -or $result.lockedFailureClearedStaleBitmap -ne $true `
    -or $result.lockRecovery -ne $true `
    -or $result.replacementLatestWon -ne $true `
    -or $result.recreateLatestWon -ne $true `
    -or $result.refreshGenerationWon -ne $true `
    -or $result.bitmapRetentionBounded -ne $true `
    -or $result.sharedStoresUntouched -ne $true `
    -or $result.fixtureBoundaryHeld -ne $true) {
    exit 1
}

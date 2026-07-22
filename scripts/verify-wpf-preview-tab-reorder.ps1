param(
    [string]$Configuration = "Release",
    [string]$OutputPath = (Join-Path $env:TEMP "photoviewer-wpf-preview-tab-reorder.json")
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj"
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net8.0-windows\PhotoViewer.Wpf.exe"

dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (Test-Path -LiteralPath $OutputPath) {
    Remove-Item -LiteralPath $OutputPath -Force
}

$process = Start-Process -FilePath $exe `
    -ArgumentList @('--preview-tab-reorder-smoke', ('"{0}"' -f $OutputPath)) `
    -WindowStyle Hidden `
    -PassThru `
    -Wait

if (Test-Path -LiteralPath $OutputPath) {
    $result = Get-Content -Raw -LiteralPath $OutputPath | ConvertFrom-Json
    $result | ConvertTo-Json -Depth 8
    if ($result.ok -ne $true -or -not $result.persistedOrderActivePin -or -not $result.restoreFocus) { exit 1 }
}
if ($process.ExitCode -ne 0) { exit $process.ExitCode }

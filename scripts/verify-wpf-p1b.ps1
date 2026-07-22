param(
    [string]$Configuration = "Release",
    [string]$OutputPath = (Join-Path $env:TEMP "photoviewer-wpf-p1b.json")
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
    -ArgumentList @('--p1b-smoke', ('"{0}"' -f $OutputPath)) `
    -WindowStyle Hidden `
    -PassThru `
    -Wait

if ($process.ExitCode -ne 0) {
    if (Test-Path -LiteralPath $OutputPath) { Get-Content -Raw -LiteralPath $OutputPath }
    exit $process.ExitCode
}

$result = Get-Content -Raw -LiteralPath $OutputPath | ConvertFrom-Json
if ($result.ok -ne $true) {
    $result | ConvertTo-Json -Depth 8
    exit 1
}

$result | ConvertTo-Json -Depth 8

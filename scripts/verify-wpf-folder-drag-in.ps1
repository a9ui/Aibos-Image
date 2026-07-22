param(
    [string]$Configuration = "Release",
    [string]$OutputPath = (Join-Path $env:TEMP "photoviewer-wpf-folder-drag-in.json")
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj"
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net8.0-windows\PhotoViewer.Wpf.exe"

dotnet build $project -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Remove-Item -LiteralPath $OutputPath -Force -ErrorAction SilentlyContinue
$process = Start-Process -FilePath $exe `
    -ArgumentList @('--folder-drag-in-smoke', ('"{0}"' -f $OutputPath)) `
    -WindowStyle Hidden -PassThru -Wait

if (-not (Test-Path -LiteralPath $OutputPath)) {
    throw "WPF folder drag-in smoke did not produce $OutputPath"
}

$result = Get-Content -Raw -LiteralPath $OutputPath | ConvertFrom-Json
$result | ConvertTo-Json -Depth 8
if ($process.ExitCode -ne 0 -or $result.ok -ne $true -or -not $result.sourceUntouched -or -not $result.isolated -or -not $result.passive) {
    exit 1
}

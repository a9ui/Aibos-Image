param(
    [string]$Configuration = "Release",
    [string]$OutputPath = (Join-Path $env:TEMP "photoviewer-wpf-folder-drag-in.json"),
    [string]$DotNetPath = "dotnet"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj"
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net10.0-windows\PhotoViewer.Wpf.exe"
$localDotNet10 = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet10\dotnet.exe"
if ($DotNetPath -eq "dotnet" -and (Test-Path -LiteralPath $localDotNet10 -PathType Leaf)) {
    $DotNetPath = $localDotNet10
}
$dotNetExecutable = (Get-Command $DotNetPath -ErrorAction Stop).Source
$dotNetRoot = Split-Path -Parent $dotNetExecutable
$previousDotNetRoot = $env:DOTNET_ROOT
$previousDotNetRootX64 = $env:DOTNET_ROOT_X64

try {
    $env:DOTNET_ROOT = $dotNetRoot
    $env:DOTNET_ROOT_X64 = $dotNetRoot
    & $dotNetExecutable build $project -c $Configuration --nologo
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
    if ($process.ExitCode -ne 0 -or $result.ok -ne $true -or -not $result.sourceUntouched -or -not $result.isolated -or -not $result.passive -or -not $result.dropOverlayClickDismissed -or -not $result.surfaceContract) {
        exit 1
    }
}
finally {
    $env:DOTNET_ROOT = $previousDotNetRoot
    $env:DOTNET_ROOT_X64 = $previousDotNetRootX64
}

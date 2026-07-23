param(
    [string]$Configuration = 'Release',
    [string]$OutputPath = (Join-Path $env:TEMP 'aibos-album-library.png'),
    [int]$Width = 1120,
    [int]$Height = 760,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net10.0-windows\PhotoViewer.Wpf.exe"
$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)

if (-not $SkipBuild) {
    dotnet build $project -c $Configuration
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$outputDirectory = Split-Path -Parent $fullOutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}
if (Test-Path -LiteralPath $fullOutputPath) {
    Remove-Item -LiteralPath $fullOutputPath -Force
}

$process = Start-Process -FilePath $exe -ArgumentList @(
    '--album-library-shot', ('"{0}"' -f $fullOutputPath),
    '--shot-width', $Width,
    '--shot-height', $Height
) -WindowStyle Hidden -PassThru -Wait

if ($process.ExitCode -ne 0) {
    throw "Album Library capture failed with exit code $($process.ExitCode)."
}
if (-not (Test-Path -LiteralPath $fullOutputPath -PathType Leaf)) {
    throw 'Album Library capture produced no PNG.'
}
$bytes = [IO.File]::ReadAllBytes($fullOutputPath)
if ($bytes.Length -lt 8 -or $bytes[0] -ne 0x89 -or $bytes[1] -ne 0x50 -or $bytes[2] -ne 0x4e -or $bytes[3] -ne 0x47) {
    throw 'Album Library capture is not a valid PNG.'
}

[pscustomobject]@{
    ok = $true
    path = $fullOutputPath
    width = [Math]::Min(3840, [Math]::Max(980, $Width))
    height = [Math]::Min(2160, [Math]::Max(560, $Height))
    bytes = $bytes.Length
} | ConvertTo-Json

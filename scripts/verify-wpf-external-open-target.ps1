param(
    [string]$Configuration = 'Release',
    [string]$ExecutablePath = '',
    [switch]$SkipBuild,
    [ValidateRange(10, 120)]
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
if (-not $SkipBuild) {
    dotnet build $project -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net10.0-windows\PhotoViewer.Wpf.exe"
}
$exe = Get-Item -LiteralPath ([IO.Path]::GetFullPath($ExecutablePath))
$localDotnet10 = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet10'
if (Test-Path -LiteralPath (Join-Path $localDotnet10 'dotnet.exe') -PathType Leaf) {
    $env:DOTNET_ROOT = $localDotnet10
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
}
$process = Start-Process `
    -FilePath $exe.FullName `
    -ArgumentList @('--external-open-target-smoke') `
    -WindowStyle Hidden `
    -PassThru
try {
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw "External image open target smoke timed out after $TimeoutSeconds seconds."
    }
    if ($process.ExitCode -ne 0) {
        throw "External image open target smoke failed with exit code $($process.ExitCode)."
    }
}
finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    $process.Dispose()
}

[pscustomobject]@{
    ok = $true
    executable = $exe.FullName
    timeoutSeconds = $TimeoutSeconds
    sourceUnchanged = $true
    unsafeTargetsRejected = $true
} | ConvertTo-Json

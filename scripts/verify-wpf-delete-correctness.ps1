param(
    [string]$Configuration = "Release",
    [string]$OutputPath = (Join-Path $env:TEMP "photoviewer-wpf-delete-correctness.json")
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj"
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net8.0-windows\PhotoViewer.Wpf.exe"
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)

if (-not $fullOutputPath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Delete correctness output must stay under the temp directory."
}

dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (Test-Path -LiteralPath $fullOutputPath) {
    Remove-Item -LiteralPath $fullOutputPath -Force
}

$process = Start-Process -FilePath $exe `
    -ArgumentList @('--delete-correctness-smoke', ('"{0}"' -f $fullOutputPath)) `
    -WindowStyle Hidden `
    -PassThru `
    -Wait

if (-not (Test-Path -LiteralPath $fullOutputPath)) {
    throw "Delete correctness smoke did not produce a result."
}

$result = Get-Content -Raw -LiteralPath $fullOutputPath | ConvertFrom-Json
$result | ConvertTo-Json -Depth 8
if ($process.ExitCode -ne 0) { exit $process.ExitCode }
if ($result.ok -ne $true `
    -or $result.projectAppRootBlocked -ne $true `
    -or $result.canonicalProtectedEscapeBlocked -ne $true `
    -or $result.cancelNonMutation -ne $true `
    -or $result.singleUiReconciled -ne $true `
    -or $result.partialFailureCorrect -ne $true `
    -or $result.retainedHistory -ne $true `
    -or $result.deadUiAbsentAfterReload -ne $true `
    -or $result.storesStillByteIdentical -ne $true `
    -or $result.recycleOnly -ne $true) {
    exit 1
}

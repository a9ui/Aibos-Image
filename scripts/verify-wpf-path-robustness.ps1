param(
    [string]$Configuration = 'Release',
    [string]$OutputPath = (Join-Path $env:TEMP ('photoviewer-wpf-path-robustness-' + [guid]::NewGuid().ToString('N') + '.json'))
)

$ErrorActionPreference = 'Stop'
if ($OutputPath.Contains('"')) { throw 'OutputPath cannot contain a double quote.' }

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net8.0-windows\PhotoViewer.Wpf.exe"
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
if (-not $fullOutputPath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputPath must be under the temp directory.'
}

dotnet build $project -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Remove-Item -LiteralPath $fullOutputPath -ErrorAction SilentlyContinue
$process = Start-Process -FilePath $exe `
    -ArgumentList @('--path-robustness-smoke', ('"{0}"' -f $fullOutputPath)) `
    -WindowStyle Hidden -Wait -PassThru

if (-not (Test-Path -LiteralPath $fullOutputPath)) {
    throw "WPF path robustness process exited without producing $fullOutputPath"
}

$result = Get-Content -Raw -LiteralPath $fullOutputPath -Encoding UTF8 | ConvertFrom-Json
$failures = @()
if ($process.ExitCode -ne 0) { $failures += "process exit code $($process.ExitCode)" }
if ($result.ok -ne $true) { $failures += 'result.ok was false' }
foreach ($property in @(
    'longPathOverLegacyLimit', 'scanComplete', 'caseDeduped', 'mixedWarningsVisible', 'allWarningKindsRetained',
    'unicodeSearch', 'longPathSearch', 'caseInsensitiveSearch', 'validPreviews',
    'lockedRecoverable', 'corruptRecoverable', 'modalAndTabSafe', 'explorerSafe',
    'fileDropSafe', 'folderDropSafe', 'disappearedReconciled', 'lockRecovery',
    'refreshedMixedWarningsVisible', 'protectedBlocked', 'fakeRecycleAccepted',
    'sourceUntouched', 'readOnlyPreserved', 'passive', 'missingStayedMissing', 'isolated'
)) {
    if ($result.$property -ne $true) { $failures += "$property was not true" }
}
if ([int]$result.longPathLength -le 260) { $failures += 'long path fixture did not exceed 260 characters' }
if (@($result.recycleCalls).Count -ne 1) { $failures += 'fake recycle backend did not receive exactly one allowed call' }

$result | ConvertTo-Json -Depth 12
if ($failures.Count -gt 0) {
    throw ('WPF path robustness gate failed: ' + ($failures -join '; '))
}

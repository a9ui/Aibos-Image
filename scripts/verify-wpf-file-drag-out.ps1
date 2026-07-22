param(
    [string]$Configuration = 'Release',
    [string]$OutputPath = (Join-Path $env:TEMP ('photoviewer-wpf-file-drag-out-' + [guid]::NewGuid().ToString('N') + '.json'))
)

$ErrorActionPreference = 'Stop'
if ($OutputPath.Contains('"')) { throw 'OutputPath cannot contain a double quote.' }

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net8.0-windows\PhotoViewer.Wpf.exe"

dotnet build $project -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Remove-Item -LiteralPath $OutputPath -ErrorAction SilentlyContinue
$process = Start-Process -FilePath $exe `
    -ArgumentList @('--file-drag-out-smoke', ('"{0}"' -f $OutputPath)) `
    -WindowStyle Hidden -Wait -PassThru

if (-not (Test-Path -LiteralPath $OutputPath)) {
    throw "WPF file drag-out process exited without producing $OutputPath"
}

$result = Get-Content -Raw -LiteralPath $OutputPath | ConvertFrom-Json
$failures = @()
if ($process.ExitCode -ne 0) { $failures += "process exit code $($process.ExitCode)" }
if ($result.ok -ne $true) { $failures += 'result.ok was false' }
if ($result.selectedPayload.fileDropFormatPresent -ne $true -or $result.selectedPayload.surfaceContractReady -ne $true) { $failures += 'FileDrop format or source-surface contract is missing' }
if ($result.selectedPayload.exceedsThreshold -ne $true -or $result.exactThresholdRejected -ne $true) { $failures += 'system drag threshold contract failed' }
if ($result.originOnlyPayload.paths.Count -ne 1 -or $result.invalidPayload.built -ne $false) { $failures += 'selected/origin-only or invalid payload contract failed' }
if ($result.mutableStateUntouched -ne $true) { $failures += 'state, favorites, seen, or enhancement payload changed' }
if ($result.sourceCountAfter -ne 3 -or $result.enhancementJobsRead -ne 0 -or $result.enhancementCandidates -ne 0) { $failures += 'source or enhancement side effect detected' }

$result | ConvertTo-Json -Depth 8
if ($failures.Count -gt 0) {
    throw ('WPF file drag-out gate failed: ' + ($failures -join '; '))
}

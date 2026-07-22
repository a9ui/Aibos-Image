param(
    [string]$Configuration = 'Release',
    [string]$OutputPath = (Join-Path $env:TEMP ('photoviewer-wpf-explorer-reveal-' + [guid]::NewGuid().ToString('N') + '.json'))
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
    -ArgumentList @('--explorer-reveal-smoke', ('"{0}"' -f $OutputPath)) `
    -WindowStyle Hidden -Wait -PassThru

if (-not (Test-Path -LiteralPath $OutputPath)) {
    throw "WPF Explorer reveal process exited without producing $OutputPath"
}

$result = Get-Content -Raw -LiteralPath $OutputPath | ConvertFrom-Json
$failures = @()
if ($process.ExitCode -ne 0) { $failures += "process exit code $($process.ExitCode)" }
if ($result.ok -ne $true) { $failures += 'result.ok was false' }
if ($result.selected -ne $true -or $result.modalOpened -ne $true -or $result.quoteSafeFixture -ne $true) {
    $failures += 'unicode, spaces, apostrophe-safe fixture was not selected in both action contexts'
}

foreach ($surface in @('rightPreview', 'modal')) {
    $snapshot = $result.$surface
    if ($snapshot.launched -ne $true -or $snapshot.fileName -ine 'explorer.exe') {
        $failures += "$surface did not record an injected explorer.exe launch"
    }
    if ($snapshot.arguments.Count -ne 1 -or $snapshot.arguments[0] -cne $result.expectedArgument -or $snapshot.argumentsText -ne '') {
        $failures += "$surface did not use exactly one canonical /select, ArgumentList entry"
    }
    if ($snapshot.useShellExecute -ne $true -or $snapshot.automationReady -ne $true -or $snapshot.focused -ne $true) {
        $failures += "$surface shell, Automation, or focus contract failed"
    }
}

foreach ($guard in @('outsideActiveRoot', 'catalogAbsent', 'missing', 'unsupported')) {
    if ($result.$guard.accepted -ne $false -or [string]::IsNullOrWhiteSpace($result.$guard.reason)) {
        $failures += "$guard path was not rejected with a generic reason"
    }
}
if ($result.genericErrors -ne $true) { $failures += 'launcher failure/exception did not stay generic' }
if ($result.sourceUntouched -ne $true -or $result.mutableStateUntouched -ne $true) {
    $failures += 'source, state, favorites, seen, recent, or jobs fingerprint changed'
}
if ($result.passive -ne $true) { $failures += 'Explorer reveal touched enhancement work' }

$result | ConvertTo-Json -Depth 10
if ($failures.Count -gt 0) {
    throw ('WPF Explorer reveal gate failed: ' + ($failures -join '; '))
}

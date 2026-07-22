param(
    [string]$Configuration = 'Release',
    [string]$OutputPath = (Join-Path $env:TEMP ('photoviewer-wpf-gallery-enter-' + [guid]::NewGuid().ToString('N') + '.json'))
)

$ErrorActionPreference = 'Stop'
if ($OutputPath.Contains('"')) { throw 'OutputPath cannot contain a double quote.' }
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
if (-not $fullOutputPath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Gallery Enter smoke output must stay under the temp directory.'
}
$OutputPath = $fullOutputPath

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net8.0-windows\PhotoViewer.Wpf.exe"

dotnet build $project -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Remove-Item -LiteralPath $OutputPath -ErrorAction SilentlyContinue
$process = Start-Process -FilePath $exe `
    -ArgumentList @('--gallery-enter-smoke', ('"{0}"' -f $OutputPath)) `
    -WindowStyle Hidden -Wait -PassThru

if (-not (Test-Path -LiteralPath $OutputPath)) {
    throw "WPF gallery Enter process exited without producing $OutputPath"
}

$result = Get-Content -Raw -LiteralPath $OutputPath | ConvertFrom-Json
$required = @(
    'currentOrder',
    'gridOpened',
    'gridFocusReturned',
    'movedNextInCurrentOrder',
    'gridNavigationFocusReturned',
    'listOpened',
    'listFocusReturned',
    'movedPreviousInCurrentOrder',
    'listNavigationFocusReturned',
    'searchIsolation',
    'dateIsolation',
    'settingsIsolation',
    'deleteIsolation',
    'modalInputIsolation',
    'landingIsolation',
    'passive'
)
$failures = @()
if ($process.ExitCode -ne 0) { $failures += "process exit code $($process.ExitCode)" }
if ($result.ok -ne $true) { $failures += 'result.ok was false' }
foreach ($property in $required) {
    if ($result.$property -ne $true) { $failures += "$property was false" }
}

$result | ConvertTo-Json -Depth 8
if ($failures.Count -gt 0) {
    throw ('WPF gallery Enter-to-Modal gate failed: ' + ($failures -join '; '))
}

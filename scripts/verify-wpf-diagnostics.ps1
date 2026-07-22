$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$exe = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\bin\Release\net8.0-windows\PhotoViewer.Wpf.exe'
$result = Join-Path ([IO.Path]::GetTempPath()) ("photoviewer-wpf-diagnostics-" + [guid]::NewGuid().ToString('N') + '.json')
dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
try {
    $process = Start-Process -FilePath $exe -ArgumentList @('--diagnostics-smoke', ('"{0}"' -f $result)) -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "diagnostics smoke exited $($process.ExitCode)" }
    $smoke = Get-Content -Raw -LiteralPath $result | ConvertFrom-Json
    if ($smoke.ok -ne $true -or -not $smoke.privateDataAbsent) { throw "diagnostics smoke failed: $(Get-Content -Raw -LiteralPath $result)" }
    $smoke | ConvertTo-Json -Depth 8
} finally { Remove-Item -LiteralPath $result -Force -ErrorAction SilentlyContinue }

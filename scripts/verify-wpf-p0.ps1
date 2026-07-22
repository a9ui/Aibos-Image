param(
    [string]$OutputPath = (Join-Path $env:TEMP ("photoviewer-wpf-p0d-gate-" + [guid]::NewGuid().ToString('N') + ".json"))
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = Join-Path $root 'local-native\PhotoViewer.Wpf\bin\Release\net8.0-windows\PhotoViewer.Wpf.exe'
Remove-Item -LiteralPath $OutputPath -ErrorAction SilentlyContinue
if ($OutputPath.Contains('"')) { throw 'OutputPath cannot contain a double quote.' }
$process = Start-Process -FilePath $exe `
    -ArgumentList @('--p0d-smoke', ('"{0}"' -f $OutputPath)) `
    -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { exit $process.ExitCode }
if (-not (Test-Path -LiteralPath $OutputPath)) {
    throw "WPF P0D process exited without producing $OutputPath"
}

$result = Get-Content -Raw -LiteralPath $OutputPath | ConvertFrom-Json
if (-not $result.ok) { throw "WPF P0D gate did not pass: $($result.message)" }
$result

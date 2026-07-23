param(
    [string]$Configuration = "Release",
    [string]$OutputPath = (Join-Path $env:TEMP "photoviewer-wpf-formats.json")
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj"
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net10.0-windows\PhotoViewer.Wpf.exe"
$fixtureRoot = Join-Path $env:TEMP ("photoviewer-wpf-formats-" + [Guid]::NewGuid().ToString("N"))
$generator = Join-Path $PSScriptRoot "generate-wpf-format-fixtures.py"

try {
    python $generator $fixtureRoot
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    dotnet build $project -c $Configuration
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    if (Test-Path -LiteralPath $OutputPath) {
        Remove-Item -LiteralPath $OutputPath -Force
    }

    $process = Start-Process -FilePath $exe `
        -ArgumentList @('--format-smoke', ('"{0}"' -f $OutputPath), '--fixture-folder', ('"{0}"' -f $fixtureRoot)) `
        -WindowStyle Hidden `
        -PassThru `
        -Wait

    if (Test-Path -LiteralPath $OutputPath) {
        $result = Get-Content -Raw -LiteralPath $OutputPath | ConvertFrom-Json
        $result | ConvertTo-Json -Depth 10
        if ($result.ok -ne $true) { exit 1 }
    }
    if ($process.ExitCode -ne 0) { exit $process.ExitCode }
}
finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    $resolvedTemp = [IO.Path]::GetFullPath($env:TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($resolvedFixture.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedFixture)) {
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    }
}

param(
    [string]$Configuration = 'Release',
    [string]$OutputPath = (Join-Path $env:TEMP ("aibos-gallery-order-continuity-" + [guid]::NewGuid().ToString('N') + '.json')),
    [string]$DotnetPath = 'dotnet',
    [switch]$SkipRestore,
    [ValidateRange(1, 120)]
    [int]$TimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$runRoot = Join-Path $tempRoot ('aibos-gallery-order-continuity-verifier-' + [guid]::NewGuid().ToString('N'))
$buildRoot = Join-Path $runRoot 'build'
$resultPath = [IO.Path]::GetFullPath($OutputPath)
$process = $null
$smokeRoot = $null

try {
    New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    $buildOutput = $buildRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($SkipRestore) {
        & $DotnetPath build $project -c $Configuration "-p:OutputPath=$buildOutput" --nologo --no-restore
    }
    else {
        & $DotnetPath build $project -c $Configuration "-p:OutputPath=$buildOutput" --nologo
    }
    if ($LASTEXITCODE -ne 0) { throw "WPF build failed with exit code $LASTEXITCODE." }

    $dll = Join-Path $buildRoot 'PhotoViewer.Wpf.dll'
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        throw "WPF assembly was not found: $dll"
    }
    if (Test-Path -LiteralPath $resultPath) {
        Remove-Item -LiteralPath $resultPath -Force
    }

    $process = Start-Process -FilePath $DotnetPath -ArgumentList @(
        ('"{0}"' -f $dll),
        '--gallery-order-continuity-smoke',
        ('"{0}"' -f $resultPath)
    ) -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill($true)
        $process.WaitForExit()
        throw "Gallery order continuity smoke exceeded $TimeoutSeconds seconds."
    }
    $result = if (Test-Path -LiteralPath $resultPath -PathType Leaf) {
        Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    }
    else {
        $null
    }
    if ($null -ne $result) {
        $smokeRoot = [string]$result.smokeRoot
    }
    if ($process.ExitCode -ne 0 -or $null -eq $result) {
        $details = if (Test-Path -LiteralPath $resultPath) {
            Get-Content -Raw -LiteralPath $resultPath
        }
        else {
            'no result file'
        }
        throw "Gallery order continuity smoke failed with exit code $($process.ExitCode): $details"
    }

    if (
        $result.ok -ne $true -or
        $result.moveOnly -ne $true -or
        $result.thumbnailsRetained -ne $true -or
        $result.modalMovedToPinnedNeighbor -ne $true -or
        $result.modalReturnPreserved -ne $true -or
        $result.returnedToTop -ne $true -or
        $result.toolbarContract -ne $true -or
        $result.sourcesUnchanged -ne $true
    ) {
        throw "Gallery order continuity contract failed: $(Get-Content -Raw -LiteralPath $resultPath)"
    }

    $result | ConvertTo-Json -Depth 8
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $process.Kill($true)
        $process.WaitForExit()
    }
    foreach ($candidate in @($smokeRoot, $runRoot)) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        $resolved = [IO.Path]::GetFullPath($candidate)
        $parent = [IO.Path]::GetFullPath((Split-Path -Parent $resolved)).TrimEnd('\', '/')
        $leaf = Split-Path -Leaf $resolved
        if (
            $parent.Equals($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
            $leaf -match '^aibos-gallery-order-continuity-(smoke|verifier)-[A-Za-z0-9][A-Za-z0-9.]*$'
        ) {
            Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
        }
        else {
            throw "Refusing to remove unexpected verifier path: $resolved"
        }
    }
}

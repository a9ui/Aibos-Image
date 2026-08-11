param(
    [string]$Configuration = 'Release',
    [string]$OutputPath = (Join-Path $env:TEMP 'aibos-wpf-original-aspect-reflow.json'),
    [switch]$SkipBuild,
    [ValidateRange(30, 600)]
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net10.0-windows\PhotoViewer.Wpf.exe"
$resultPath = [IO.Path]::GetFullPath($OutputPath)
$dotnet = 'dotnet.exe'
$localDotnet10 = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet10\dotnet.exe'
if (Test-Path -LiteralPath $localDotnet10 -PathType Leaf) {
    $dotnet = $localDotnet10
}
$dotnetRootBefore = [Environment]::GetEnvironmentVariable('DOTNET_ROOT')
$dotnetRootX64Before = [Environment]::GetEnvironmentVariable('DOTNET_ROOT_X64')
$dotnetRoot = if ([IO.Path]::IsPathRooted($dotnet)) {
    Split-Path -Parent $dotnet
}
else {
    $null
}

try {
    if ($dotnetRoot) {
        [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $dotnetRoot)
        [Environment]::SetEnvironmentVariable('DOTNET_ROOT_X64', $dotnetRoot)
    }

    if (-not $SkipBuild) {
        & $dotnet build $project -c $Configuration
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    $directory = Split-Path -Parent $resultPath
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    if (Test-Path -LiteralPath $resultPath) {
        Remove-Item -LiteralPath $resultPath -Force
    }

    $smoke = Start-Process -FilePath $exe -ArgumentList @(
        '--aspect-smoke', ('"{0}"' -f $resultPath)
    ) -WindowStyle Hidden -PassThru
    if (-not $smoke.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $smoke.Id -Force -ErrorAction SilentlyContinue
        throw "Original-aspect reflow smoke exceeded the $TimeoutSeconds second timeout."
    }
    $smoke.Refresh()

    if ($smoke.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        throw "Original-aspect reflow smoke failed with exit code $($smoke.ExitCode)."
    }

    $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    $required = @(
        'MetadataReady',
        'MetadataLayoutCoalesced',
        'MetadataSelectionRaceRecovered',
        'OriginalShape',
        'SquareShape',
        'PortraitShape',
        'OrderStable',
        'SelectionStable',
        'ZoomComposes',
        'Persistence'
    )
    $missing = @($required | Where-Object { $result.$_ -ne $true })
    if ($result.Ok -ne $true -or $missing.Count -gt 0) {
        throw "Original-aspect reflow contract failed: $($missing -join ', ')."
    }

    $landscapeRatio = $result.OriginalLandscape.CardHeight / $result.OriginalLandscape.CardWidth
    $squareRatio = $result.OriginalSquare.CardHeight / $result.OriginalSquare.CardWidth
    $portraitRatio = $result.OriginalPortrait.CardHeight / $result.OriginalPortrait.CardWidth
    if ([Math]::Abs($landscapeRatio - (2 / 3)) -ge 0.03 `
        -or [Math]::Abs($squareRatio - 1) -ge 0.03 `
        -or [Math]::Abs($portraitRatio - 1.5) -ge 0.03) {
        throw "Original card ratios do not match decoded source ratios."
    }

    [pscustomobject]@{
        Result = 'PASS'
        Message = $result.Message
        MetadataReady = $result.MetadataReady
        MetadataLayoutPublishCount = $result.MetadataLayoutPublishCount
        MetadataLayoutCoalesced = $result.MetadataLayoutCoalesced
        MetadataRaceDiscarded = $result.MetadataRaceDiscarded
        MetadataRaceFallbackCount = $result.MetadataRaceFallbackCount
        MetadataSelectionRaceRecovered = $result.MetadataSelectionRaceRecovered
        LandscapeRatio = [Math]::Round($landscapeRatio, 3)
        SquareRatio = [Math]::Round($squareRatio, 3)
        PortraitRatio = [Math]::Round($portraitRatio, 3)
        OrderStable = $result.OrderStable
        SelectionStable = $result.SelectionStable
        ZoomComposes = $result.ZoomComposes
        Persistence = $result.Persistence
    } | Format-List
}
finally {
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $dotnetRootBefore)
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT_X64', $dotnetRootX64Before)
}

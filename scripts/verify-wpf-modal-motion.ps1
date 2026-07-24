param(
    [string]$Configuration = 'Release',
    [string]$DotNetPath = 'dotnet',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net10.0-windows\PhotoViewer.Wpf.exe"
$dotNetExecutable = (Get-Command $DotNetPath -ErrorAction Stop).Source
$dotNetRoot = Split-Path -Parent $dotNetExecutable
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ("aibos-wpf-modal-motion-" + [guid]::NewGuid().ToString('N'))))
$runParent = [IO.Path]::GetDirectoryName($runRoot)
$runLeaf = [IO.Path]::GetFileName($runRoot)
if (-not [string]::Equals($runParent, $tempRoot, [StringComparison]::OrdinalIgnoreCase) `
    -or $runLeaf -notmatch '^aibos-wpf-modal-motion-[0-9a-f]{32}$') {
    throw "Run root escaped the exact TEMP boundary: $runRoot"
}

$imagesRoot = Join-Path $runRoot 'images'
$storesRoot = Join-Path $runRoot 'stores'
$transformResultPath = Join-Path $runRoot 'modal-transform.json'
$panResultPath = Join-Path $runRoot 'modal-pan.json'
$environmentPaths = [ordered]@{
    PHOTOVIEWER_WPF_STATE_PATH = (Join-Path $storesRoot 'state.json')
    PHOTOVIEWER_WPF_FAVORITES_PATH = (Join-Path $storesRoot 'favorites.json')
    PHOTOVIEWER_WPF_SEEN_PATH = (Join-Path $storesRoot 'seen.json')
    PHOTOVIEWER_WPF_RECENT_PATH = (Join-Path $storesRoot 'recent-folders.json')
    PHOTOVIEWER_WPF_ALBUMS_PATH = (Join-Path $storesRoot 'albums.json')
    PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH = (Join-Path $storesRoot 'search-history.json')
    PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH = (Join-Path $storesRoot 'enhance\jobs.json')
    AIBOS_SHARED_ROOT_LOCATOR_PATH = (Join-Path $storesRoot 'shared-root.v1.json')
    DOTNET_ROOT = $dotNetRoot
    DOTNET_ROOT_X64 = $dotNetRoot
}
$previousEnvironment = @{}
$processes = @()

function Invoke-MotionSmoke {
    param(
        [Parameter(Mandatory = $true)][string]$Mode,
        [Parameter(Mandatory = $true)][string]$ResultPath
    )

    $stdoutPath = $ResultPath + '.stdout.log'
    $stderrPath = $ResultPath + '.stderr.log'
    $process = Start-Process -FilePath $exe `
        -ArgumentList @($Mode, ('"{0}"' -f $ResultPath), '--folder', ('"{0}"' -f $imagesRoot), '--select-index', '0') `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -WindowStyle Hidden `
        -PassThru `
        -Wait
    $script:processes += $process
    if ($process.ExitCode -ne 0) {
        $stderr = if (Test-Path -LiteralPath $stderrPath) { Get-Content -Raw -LiteralPath $stderrPath } else { 'no stderr' }
        throw "$Mode exited $($process.ExitCode): $stderr"
    }
    if (-not (Test-Path -LiteralPath $ResultPath -PathType Leaf)) {
        throw "$Mode did not produce its result."
    }
    return Get-Content -Raw -LiteralPath $ResultPath | ConvertFrom-Json
}

if (-not $SkipBuild) {
    & $DotNetPath build $project -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "WPF executable was not found: $exe"
}

try {
    New-Item -ItemType Directory -Path $imagesRoot, $storesRoot -Force | Out-Null
    $pngBytes = [Convert]::FromBase64String(
        'iVBORw0KGgoAAAANSUhEUgAAAAQAAAADCAIAAAA7l3agAAAAFElEQVR4nGPkEpFjQAJMDKiAVD4AANMABQ+5f2QAAAAASUVORK5CYII=')
    foreach ($index in 0..2) {
        [IO.File]::WriteAllBytes((Join-Path $imagesRoot ("motion-{0:D2}.png" -f $index)), $pngBytes)
    }
    $sourceHashesBefore = @(Get-ChildItem -LiteralPath $imagesRoot -File | Sort-Object Name | Get-FileHash -Algorithm SHA256)

    foreach ($name in $environmentPaths.Keys) {
        $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, $environmentPaths[$name])
    }

    $transform = Invoke-MotionSmoke -Mode '--modal-transform-smoke' -ResultPath $transformResultPath
    $pan = Invoke-MotionSmoke -Mode '--modal-pan-smoke' -ResultPath $panResultPath

    $failures = @()
    if ($transform.ok -ne $true) { $failures += "transform: $($transform.message)" }
    if ($transform.fineWheelZoomed -ne $true) { $failures += 'fine wheel delta was not accepted' }
    if ([double]$transform.fineWheelFactor -le 1 `
        -or [double]$transform.fineWheelFactor -ge [double]$transform.standardWheelFactor) {
        $failures += "fine wheel factor was not proportional ($($transform.fineWheelFactor))"
    }
    if ([Math]::Abs([double]$transform.standardWheelFactor - 1.08) -gt 0.0001) {
        $failures += "standard wheel factor was $($transform.standardWheelFactor)"
    }
    if ($pan.ok -ne $true) { $failures += "pan: $($pan.message)" }
    if ($pan.cadence.available -ne $true `
        -or [int]$pan.cadence.acceptedUpdates -ne 48 `
        -or [int]$pan.cadence.inputUpdates -ne 48) {
        $failures += 'the synthetic drag did not accept all 48 raw input updates'
    }
    if ($pan.cadence.queuedBeforeYield -ne $true `
        -or $pan.cadence.queuedAfterYield -ne $false `
        -or [int]$pan.cadence.visualUpdates -lt 1 `
        -or [int]$pan.cadence.visualUpdates -gt 2) {
        $failures += "drag input was not coalesced to one render frame ($($pan.cadence.visualUpdates) visual updates)"
    }
    if ([Math]::Abs([double]$pan.cadence.modelPanX - [double]$pan.cadence.visualPanX) -gt 0.0001 `
        -or [Math]::Abs([double]$pan.cadence.modelPanY - [double]$pan.cadence.visualPanY) -gt 0.0001) {
        $failures += 'the final presented pan did not match the latest input'
    }

    $sourceHashesAfter = @(Get-ChildItem -LiteralPath $imagesRoot -File | Sort-Object Name | Get-FileHash -Algorithm SHA256)
    $sourceUnchanged = $sourceHashesBefore.Count -eq $sourceHashesAfter.Count
    for ($index = 0; $sourceUnchanged -and $index -lt $sourceHashesBefore.Count; $index++) {
        $sourceUnchanged = $sourceHashesBefore[$index].Hash -eq $sourceHashesAfter[$index].Hash
    }
    if (-not $sourceUnchanged) { $failures += 'the synthetic source images changed' }
    if (Test-Path -LiteralPath $environmentPaths.PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH) {
        $failures += 'passive modal motion created Enhancement jobs'
    }

    [pscustomobject]@{
        ok = $failures.Count -eq 0
        fineWheelFactor = [double]$transform.fineWheelFactor
        standardWheelFactor = [double]$transform.standardWheelFactor
        rawPanUpdates = [int]$pan.cadence.inputUpdates
        visualPanUpdates = [int]$pan.cadence.visualUpdates
        finalPanMatched = [Math]::Abs([double]$pan.cadence.modelPanX - [double]$pan.cadence.visualPanX) -le 0.0001 `
            -and [Math]::Abs([double]$pan.cadence.modelPanY - [double]$pan.cadence.visualPanY) -le 0.0001
        sourceUnchanged = $sourceUnchanged
        enhancementJobsCreated = Test-Path -LiteralPath $environmentPaths.PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH
    } | ConvertTo-Json -Depth 6

    if ($failures.Count -gt 0) {
        throw ('WPF modal motion gate failed: ' + ($failures -join '; '))
    }
}
finally {
    foreach ($name in $environmentPaths.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name])
    }
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        $resolvedParent = [IO.Path]::GetDirectoryName($resolvedRunRoot)
        $resolvedLeaf = [IO.Path]::GetFileName($resolvedRunRoot)
        if ([string]::Equals($resolvedParent, $tempRoot, [StringComparison]::OrdinalIgnoreCase) `
            -and $resolvedLeaf -match '^aibos-wpf-modal-motion-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
        }
        else {
            throw "Refusing to clean unexpected run root: $resolvedRunRoot"
        }
    }
}

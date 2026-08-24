[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$DotNetPath = 'dotnet',
    [switch]$SkipBuild,
    [switch]$StaticOnly,
    [switch]$NoRestore,
    [ValidateRange(10, 120)]
    [int]$OverallTimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$contractPath = Join-Path $repoRoot 'contracts\enhancement-video-tools-v2.json'
$fixturePath = Join-Path $repoRoot 'contracts\fixtures\enhancement-video-tools-v2-reader-v1.json'
$finishContractPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\VideoFinishV2Contract.cs'
$surfacePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.VideoFinishV2.cs'
$mainWindowPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.xaml.cs'
$smokePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\App.VideoFinishV2Smoke.cs'
$entryPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.VideoTools.cs'
$xamlPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.xaml'
$appPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\App.xaml.cs'
$jaPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\Localization\StringResources.ja.xaml'
$enPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\Localization\StringResources.en.xaml'
$projectPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'

foreach ($requiredPath in @(
    $contractPath,
    $fixturePath,
    $finishContractPath,
    $surfacePath,
    $mainWindowPath,
    $smokePath,
    $entryPath,
    $xamlPath,
    $appPath,
    $jaPath,
    $enPath,
    $projectPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Video Finish v2 source is missing: $requiredPath"
    }
}

$contractHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $contractPath).Hash.ToLowerInvariant()
$fixtureHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $fixturePath).Hash.ToLowerInvariant()
if ($contractHash -cne 'eee8bc37402923717936576402357d93d8150b7f6874852f8a0e18dde3e33eb7' `
    -or $fixtureHash -cne '5944bee764d363d95aaa67f79444511eabc2aebaca90bccd2a88b2e095685c5a') {
    throw "Unexpected Video Tools v2 authority. contract=$contractHash fixture=$fixtureHash"
}

$contract = Get-Content -Raw -Encoding UTF8 -LiteralPath $finishContractPath
$surface = Get-Content -Raw -Encoding UTF8 -LiteralPath $surfacePath
$mainWindow = Get-Content -Raw -Encoding UTF8 -LiteralPath $mainWindowPath
$smoke = Get-Content -Raw -Encoding UTF8 -LiteralPath $smokePath
$entry = Get-Content -Raw -Encoding UTF8 -LiteralPath $entryPath
$xaml = Get-Content -Raw -Encoding UTF8 -LiteralPath $xamlPath
$app = Get-Content -Raw -Encoding UTF8 -LiteralPath $appPath

if ($contract -notmatch 'TryBuildFinishRequest' `
    -or $contract -notmatch 'IsExactReadyHealth' `
    -or $contract -notmatch 'aibos-video-finish-ready-v1' `
    -or $contract -notmatch 'aibos-video-finish-mode-ready-v1' `
    -or $contract -notmatch 'maximumConcurrentGpuJobs[\s\S]{0,180}\b1\b' `
    -or $contract -notmatch 'fullVideoPtsSequencePreserved' `
    -or $contract -notmatch 'sourceAudioPacketIdentityPreserved' `
    -or $contract -notmatch 'silentModeFallback' `
    -or $contract -notmatch 'explicitScale4Required') {
    throw 'The Finish v2 exact request, planner, or paired readiness parser is incomplete.'
}
if ($surface -notmatch 'TryCaptureDisplayedVideoEditV2Source' `
    -or $surface -notmatch 'CaptureModalVideoEditV2ExplicitSourceAsync' `
    -or $surface -notmatch 'BuildProbeRequestJson' `
    -or $surface -notmatch 'TryParseProbeResponse' `
    -or $surface -notmatch 'SendPassiveEnhancementReadAsync\("api/enhance/health"\)' `
    -or $surface -notmatch 'SendEnhancementEnqueueAsync' `
    -or $surface -notmatch 'requireExactHealthValidation:\s*true' `
    -or $surface -notmatch 'AcquireVideoDurablePublishLease' `
    -or $surface -notmatch 'PinVideoSourceForDurablePublish' `
    -or $surface -notmatch 'RecordPendingVideoSourceDependency' `
    -or $surface -notmatch 'RecordActiveVideoSourceDependency' `
    -or $surface -notmatch 'ValidateModalVideoFinishV2BeforePublishAsync') {
    throw 'The explicit Finish v2 probe or durable Start interlock is incomplete.'
}
$passiveCapture = [regex]::Match(
    $surface,
    'private bool TryCaptureDisplayedVideoFinishV2Source[\s\S]*?private void SyncModalVideoFinishV2EntryPresentation',
    [Text.RegularExpressions.RegexOptions]::CultureInvariant).Value
$passiveCaptureCode = $passiveCapture -replace '(?m)//.*$', ''
if ([string]::IsNullOrWhiteSpace($passiveCaptureCode) `
    -or $passiveCaptureCode -match 'File\.|Directory\.|FileInfo|ResolveFinal|_resolveFinalPath|Hash|SendEnhancement|Open\(') {
    throw 'Passive Finish board capture must copy only pinned or hydrated source state.'
}
if ($entry -notmatch 'OpenModalVideoFinish_Click[\s\S]{0,180}OpenModalVideoFinishV2Board' `
    -or $entry -notmatch 'SyncModalVideoFinishV2EntryPresentation') {
    throw 'The production Finish entry was not rerouted from the frozen v1 board.'
}
if ($mainWindow -notmatch 'CloseTopmostOverlay[\s\S]{0,2200}ModalVideoFinishV2Popup[\s\S]{0,300}CloseModalVideoFinishV2Board' `
    -or $mainWindow -notmatch 'OnPreviewKeyDown[\s\S]{0,2600}ModalVideoFinishV2Popup[\s\S]{0,260}Key\.Escape[\s\S]{0,300}CloseModalVideoFinishV2Board' `
    -or $mainWindow -notmatch 'CloseModal\(bool restoreFocus[\s\S]{0,1800}ModalVideoFinishV2Popup[\s\S]{0,300}CloseModalVideoFinishV2Board' `
    -or $mainWindow -notmatch 'IsModalChromeInteractionTarget[\s\S]{0,900}ModalVideoFinishV2Popup' `
    -or $mainWindow -notmatch 'DismissModalSettingsBoardForWindowChrome[\s\S]{0,500}ModalVideoFinishV2Popup[\s\S]{0,300}CloseModalVideoFinishV2Board') {
    throw 'Finish v2 is missing a modal close, Escape, focus, or window-chrome lifecycle seam.'
}

$boardMatch = [regex]::Match(
    $xaml,
    '<Grid x:Name="ModalVideoFinishV2Popup"[\s\S]*?</Grid>\s*<Grid x:Name="ModalVideoEditV2Popup"',
    [Text.RegularExpressions.RegexOptions]::CultureInvariant)
if (-not $boardMatch.Success) {
    throw 'The independent Finish v2 board is missing before the Edit board.'
}
$board = $boardMatch.Value
foreach ($name in @(
    'ModalVideoFinishV2ProbeButton',
    'ModalVideoFinishV2ModeComboBox',
    'ModalVideoFinishV2ScaleComboBox',
    'ModalVideoFinishV2PlanText',
    'ModalVideoFinishV2PolicyText',
    'ModalVideoFinishV2EstimateText',
    'ModalVideoFinishV2ReadinessButton',
    'ModalVideoFinishV2StartButton')) {
    if ($board -notmatch ('x:Name="' + [regex]::Escape($name) + '"')) {
        throw "The Finish v2 board is missing $name."
    }
}
foreach ($tag in @('fast', 'standard', 'quality', '2', '4')) {
    if ($board -notmatch ('Tag="' + [regex]::Escape($tag) + '"')) {
        throw "The Finish v2 board is missing semantic choice $tag."
    }
}
if ($board -match 'NVIDIA|SeedVR|NanoVSR|Bernini|VACE|MiniMax|H3' `
    -or $board -match 'x:Name="[^"]*(Prompt|Style|Step)[^"]*"') {
    throw 'Backend identities or unrelated Prompt, Style, or STEP controls leaked into Finish v2.'
}
if ($xaml -notmatch 'ModalVideoEditV2ExternalContextMenu[\s\S]{0,1800}OpenModalVideoFinish_Click' `
    -or $xaml -notmatch 'x:Name="ModalContextVideoFinishV2"[\s\S]{0,300}OpenModalVideoFinish_Click') {
    throw 'Managed and displayed-file context menus do not share the Finish v2 entry.'
}

$requiredKeys = @(
    'UiVideoFinishV2Action',
    'UiVideoFinishV2ActionHelp',
    'UiVideoFinishV2BoardAutomation',
    'UiVideoFinishV2ProbeAction',
    'UiVideoFinishV2ModeFast',
    'UiVideoFinishV2ModeStandard',
    'UiVideoFinishV2ModeQuality',
    'UiVideoFinishV2Scale2',
    'UiVideoFinishV2Scale4',
    'UiVideoFinishV2PreservePolicy',
    'UiVideoFinishV2ReadinessAction',
    'UiVideoFinishV2StartAction',
    'UiVideoFinishV2SavedForDelivery',
    'UiVideoFinishV2Queued')
foreach ($resourcePath in @($jaPath, $enPath)) {
    [xml]$resource = Get-Content -Raw -Encoding UTF8 -LiteralPath $resourcePath
    $keys = @($resource.ResourceDictionary.String | ForEach-Object {
        $_.GetAttribute('Key', 'http://schemas.microsoft.com/winfx/2006/xaml')
    })
    foreach ($key in $requiredKeys) {
        if ($keys -notcontains $key) {
            throw "Missing localized Finish v2 resource $key in $resourcePath"
        }
    }
}
if ($app -notmatch '--video-finish-v2-smoke' `
    -or $app -notmatch 'CaptureVideoFinishV2Smoke') {
    throw 'The focused Finish v2 smoke dispatch is missing.'
}
foreach ($token in @(
    'requestExact',
    'plannerExact',
    'healthExact',
    'requestedModeNoFallback',
    'passiveOpen',
    'externalProbeExact',
    'currentDisabledNoPublish',
    'entryRerouted',
    'lifecycleExact',
    'sourceUntouched')) {
    if ($smoke -notmatch [regex]::Escape($token)) {
        throw "The focused Finish v2 smoke is missing $token."
    }
}

if ($StaticOnly) {
    [pscustomobject]@{
        ok = $true
        staticOnly = $true
        operation = 'video'
        kind = 'finish'
        defaultMode = 'standard'
        defaultScale = 2
        productionWriterEnabled = $false
    } | ConvertTo-Json -Depth 5
    return
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot (
    'aibos-wpf-video-finish-v2-' + [guid]::NewGuid().ToString('N'))))
$buildRoot = Join-Path $runRoot 'build'
$resultPath = Join-Path $runRoot 'result.json'
$stdoutPath = Join-Path $runRoot 'stdout.log'
$stderrPath = Join-Path $runRoot 'stderr.log'
$process = $null
$previousDotNetRoot = [Environment]::GetEnvironmentVariable('DOTNET_ROOT', 'Process')
$previousDotNetRootX64 = [Environment]::GetEnvironmentVariable('DOTNET_ROOT_X64', 'Process')
if (-not $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Verifier root must stay under TEMP.'
}

try {
    New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    if ($DotNetPath -eq 'dotnet') {
        $localDotNet10 = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet10\dotnet.exe'
        if (Test-Path -LiteralPath $localDotNet10 -PathType Leaf) {
            $DotNetPath = $localDotNet10
        }
    }
    $dotNetExecutable = (Get-Command $DotNetPath -ErrorAction Stop).Source
    $dotNetRoot = Split-Path -Parent $dotNetExecutable
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $dotNetRoot, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT_X64', $dotNetRoot, 'Process')
    if (-not $SkipBuild) {
        $arguments = @('build', $projectPath, '-c', $Configuration)
        if ($NoRestore) {
            $output = $buildRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
            $arguments += @("-p:OutputPath=$output", '--no-restore')
        }
        else {
            $arguments += @('--artifacts-path', $buildRoot)
        }
        $arguments += @('--nologo', '-v:minimal')
        & $dotNetExecutable @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "WPF Video Finish v2 build failed with exit code $LASTEXITCODE."
        }
    }
    $dll = if ($SkipBuild) {
        Join-Path $repoRoot ('local-native\PhotoViewer.Wpf\bin\' + $Configuration + '\net10.0-windows\PhotoViewer.Wpf.dll')
    }
    else {
        Get-ChildItem -LiteralPath $buildRoot -Recurse -Filter 'PhotoViewer.Wpf.dll' |
            Select-Object -First 1 -ExpandProperty FullName
    }
    if ([string]::IsNullOrWhiteSpace($dll) -or -not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        throw 'Built WPF DLL was not found.'
    }
    $process = Start-Process -FilePath $dotNetExecutable -ArgumentList @(
        ('"{0}"' -f $dll),
        '--video-finish-v2-smoke',
        ('"{0}"' -f $resultPath),
        '--fixture',
        ('"{0}"' -f $fixturePath)) `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -WindowStyle Hidden `
        -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds($OverallTimeoutSeconds)
    while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
    }
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        throw "WPF Video Finish v2 smoke exceeded $OverallTimeoutSeconds seconds."
    }
    $process.WaitForExit()
    $process.Refresh()
    $exitCode = [int]$process.ExitCode
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        $stderr = if (Test-Path -LiteralPath $stderrPath) {
            Get-Content -Raw -LiteralPath $stderrPath
        }
        else { 'no stderr' }
        throw "Finish v2 smoke did not produce a result. stderr=$stderr"
    }
    $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    $required = @(
        'requestExact',
        'plannerExact',
        'healthExact',
        'requestedModeNoFallback',
        'passiveOpen',
        'externalProbeExact',
        'currentDisabledNoPublish',
        'entryRerouted',
        'lifecycleExact',
        'sourceUntouched')
    $failed = @($required | Where-Object { $result.$_ -ne $true })
    if ($exitCode -ne 0 -or $result.ok -ne $true -or $failed.Count -gt 0) {
        throw ('WPF Video Finish v2 smoke failed: ' +
            ($result | ConvertTo-Json -Depth 8 -Compress) +
            '; processExitCode=' + $exitCode +
            '; failed=' + ($failed -join ','))
    }
    $result | ConvertTo-Json -Depth 8
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }
    if (Test-Path -LiteralPath $runRoot) {
        $resolved = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $runRoot).Path)
        if (-not $resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to clean a verifier root outside TEMP.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $previousDotNetRoot, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT_X64', $previousDotNetRootX64, 'Process')
}

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
$plannerPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\VideoEditV2Plan.cs'
$surfacePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.VideoEditV2.cs'
$smokePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\App.VideoEditV2Smoke.cs'
$projectPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$xamlPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.xaml'
$mainPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.xaml.cs'
$appPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\App.xaml.cs'
$jaResourcePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\Localization\StringResources.ja.xaml'
$enResourcePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\Localization\StringResources.en.xaml'

foreach ($requiredPath in @(
    $plannerPath,
    $surfacePath,
    $smokePath,
    $projectPath,
    $xamlPath,
    $mainPath,
    $appPath,
    $jaResourcePath,
    $enResourcePath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "AI video edit v2 source is missing: $requiredPath"
    }
}

$planner = Get-Content -Raw -Encoding UTF8 -LiteralPath $plannerPath
$surface = Get-Content -Raw -Encoding UTF8 -LiteralPath $surfacePath
$smoke = Get-Content -Raw -Encoding UTF8 -LiteralPath $smokePath
$xaml = Get-Content -Raw -Encoding UTF8 -LiteralPath $xamlPath
$main = Get-Content -Raw -Encoding UTF8 -LiteralPath $mainPath
$app = Get-Content -Raw -Encoding UTF8 -LiteralPath $appPath

if ($planner -notmatch 'SupportedSourceFpsValues\s*=\s*\[24, 30, 60\]' `
    -or $planner -notmatch 'MaximumSelectionSeconds\s*=\s*5' `
    -or $planner -notmatch 'EndFrameExclusive' `
    -or $planner -notmatch 'endFrameExclusive - 1' `
    -or $planner -notmatch 'selectedFrameCount > maximumSelectionFrames') {
    throw 'The pure planner does not preserve 24/30/60fps, the bounded five-second half-open range, or its final included frame.'
}
if ($planner -match 'System\.IO|HttpClient|SendEnhancement|Enqueue|SaveState|File\.|Directory\.|Process\.|Task\.') {
    throw 'The pure AI video edit planner crossed an I/O, transport, process, or persistence boundary.'
}
if ($surface -match 'HttpClient|SendEnhancement|Enqueue|SaveState|File\.|Directory\.|Process\.|Task\.Run|StartEnhancement') {
    throw 'The preview-only AI video edit surface crossed its no-transport, no-process, or no-persistence boundary.'
}
if ($surface -match 'TryFrameCountFromDuration' `
    -or $surface -notmatch 'Managed:\s*false,[\s\S]{0,180}ExactTimeline:\s*exact,[\s\S]{0,120}fpsNumerator,[\s\S]{0,120}frameCount' `
    -or $surface -notmatch 'fpsNumerator = exact \? probe!\.FpsNumerator : 0' `
    -or $surface -notmatch 'frameCount = exact \? probe!\.FrameCount : 0') {
    throw 'Dropped video metadata must stay unknown until the explicit exact probe is accepted.'
}
if ($surface -notmatch 'ModalVideoEditV2ProbeButton\.IsEnabled\s*=\s*false' `
    -or $surface -notmatch 'ModalVideoEditV2CompileButton\.IsEnabled\s*=\s*false' `
    -or $surface -notmatch 'ModalVideoEditV2StartButton\.IsEnabled\s*=\s*false' `
    -or $surface -notmatch 'ModalVideoEditV2TrimButton\.IsEnabled\s*=\s*false') {
    throw 'Unconnected probe, compiler, AI writer, and non-AI trim actions must remain honestly disabled.'
}
if ($surface -notmatch 'IsSafeModalVideoEditV2CompilerText' `
    -or $surface -notmatch 'char\.IsControl' `
    -or $surface -notmatch 'value\.Length == 64' `
    -or $surface -notmatch "character is >= '0' and <= '9'[\s\S]{0,80}or >= 'a' and <= 'f'" `
    -or $surface -notmatch 'IsSafeModalVideoEditV2CompilerRevision') {
    throw 'Malformed compiler text, revision, or lowercase SHA-256 context digest is not rejected fail-closed.'
}
if ($surface -notmatch 'TryCaptureDisplayedVideoEditV2Source\([\s\S]{0,220}current\.Managed[\s\S]{0,180}ModalContextMenu\?\.IsEnabled != false' `
    -or $surface -notmatch 'ModalVideoEditV2ExternalContextMenu\.IsOpen = true;[\s\S]{0,80}e\.Handled = true;') {
    throw 'The dedicated right-click menu must intercept only a dropped external video; managed video keeps the existing modal menu.'
}
$passiveManaged = [regex]::Match(
    $surface,
    'private bool TryCapturePassiveDisplayedManagedVideoEditV2Source[\s\S]*?private void SyncModalVideoEditV2EntryPresentation',
    [Text.RegularExpressions.RegexOptions]::CultureInvariant).Value
$passiveManagedCode = $passiveManaged -replace '(?m)//.*$', ''
if ([string]::IsNullOrWhiteSpace($passiveManagedCode) `
    -or $passiveManagedCode -match 'TryGetModalSourceTile|TryValidateManagedVideoVersion|FileInfo|_resolveFinalPath|ResolveFinal|File\.|Open\(|Hash|Probe') {
    throw 'Passive managed-video board capture must copy only the hydrated modal Job snapshot without path or media revalidation.'
}
if ($main -notmatch 'if \(sourceChanged\)[\s\S]{0,100}InvalidateModalVideoEditV2ForSourceChange\(\)' `
    -or $main -notmatch 'CloseModalVideoEditV2Board\(restoreFocus: false, stale: true\)' `
    -or $main -notmatch 'IsDescendantOrSelf\(target, ModalVideoEditV2Popup\)' `
    -or $main -notmatch 'ModalVideoEditV2Popup\?\.Visibility == Visibility\.Visible[\s\S]{0,180}key == Key\.Escape' `
    -or $main -notmatch 'private bool DismissModalSettingsBoardForWindowChrome\(\)[\s\S]{0,180}ModalVideoEditV2Popup') {
    throw 'Modal close, source navigation, chrome capture, Escape, or minimize does not close and stale the AI video edit board.'
}

$boardMatch = [regex]::Match(
    $xaml,
    '<Grid x:Name="ModalVideoEditV2Popup"[\s\S]*?</Grid>\s*<!-- image area -->',
    [Text.RegularExpressions.RegexOptions]::CultureInvariant)
if (-not $boardMatch.Success) {
    throw 'The compact AI video edit board is missing from the enlarged modal.'
}
$board = $boardMatch.Value
$requiredXamlNames = @(
    'ModalVideoEditV2Button',
    'ModalContextVideoEditV2',
    'ModalVideoEditV2ExternalContextMenu',
    'ModalVideoEditV2ProbeButton',
    'ModalVideoEditV2StartFrameTextBox',
    'ModalVideoEditV2EndFrameTextBox',
    'ModalVideoEditV2StartPreviewButton',
    'ModalVideoEditV2MiddlePreviewButton',
    'ModalVideoEditV2EndPreviewButton',
    'ModalVideoEditV2TrimButton',
    'ModalVideoEditV2InstructionTextBox',
    'ModalVideoEditV2CompileButton',
    'ModalVideoEditV2SkipReviewCheckBox',
    'ModalVideoEditV2ReviewPanel',
    'ModalVideoEditV2AudioComboBox',
    'ModalVideoEditV2StrengthComboBox',
    'ModalVideoEditV2CanvasComboBox',
    'ModalVideoEditV2StyleComboBox',
    'ModalVideoEditV2SaveStyleButton',
    'ModalVideoEditV2StepsSlider',
    'ModalVideoEditV2StartButton')
foreach ($name in $requiredXamlNames) {
    if ($xaml -notmatch ('x:Name="' + [regex]::Escape($name) + '"')) {
        throw "The AI video edit surface is missing $name."
    }
}
foreach ($tag in @('preserve', 'mute', '230400', '307200', '414720')) {
    if ($board -notmatch ('Tag="' + [regex]::Escape($tag) + '"')) {
        throw "The AI video edit semantic request control is missing tag $tag."
    }
}
if ($board -match 'Bernini|VACE|MiniMax|H3|Ref2VA|AddGuide|VideoFinish|Upscale|高画質') {
    throw 'Backend identities or the separate video enhancement lane leaked into the generic AI video edit board.'
}
if ($xaml -notmatch 'PreviewMouseRightButtonUp="ModalVideoEditV2_RightClick"' `
    -or $xaml -notmatch 'ModalContextVideoEditV2[\s\S]{0,500}OpenModalVideoEditV2_Click') {
    throw 'The enlarged modal and its existing managed-video context menu are not wired to the same edit entry.'
}

$requiredResourceKeys = @(
    'UiVideoEditV2Action',
    'UiVideoEditV2ActionHelp',
    'UiVideoEditV2BoardAutomation',
    'UiVideoEditV2BoardHelp',
    'UiVideoEditV2ChildClipNotice',
    'UiVideoEditV2ProbeAction',
    'UiVideoEditV2ProbeHelp',
    'UiVideoEditV2FpsHelp',
    'UiVideoEditV2PreviewSeekNotice',
    'UiVideoEditV2TrimAction',
    'UiVideoEditV2TrimSeparationNotice',
    'UiVideoEditV2InstructionAutomation',
    'UiVideoEditV2CompileAutomation',
    'UiVideoEditV2SkipReviewHelp',
    'UiVideoEditV2ReviewAutomation',
    'UiVideoEditV2AudioAutomation',
    'UiVideoEditV2StrengthAutomation',
    'UiVideoEditV2CanvasAutomation',
    'UiVideoEditV2StyleAutomation',
    'UiVideoEditV2StepsAutomation',
    'UiVideoEditV2ReadinessAutomation',
    'UiVideoEditV2StartHelp')
foreach ($resourcePath in @($jaResourcePath, $enResourcePath)) {
    [xml]$resource = Get-Content -Raw -Encoding UTF8 -LiteralPath $resourcePath
    $keys = @($resource.ResourceDictionary.String | ForEach-Object {
        $_.GetAttribute(
            'Key',
            'http://schemas.microsoft.com/winfx/2006/xaml')
    })
    foreach ($requiredKey in $requiredResourceKeys) {
        if ($keys -notcontains $requiredKey) {
            throw "Missing localized AI video edit resource $requiredKey in $resourcePath"
        }
    }
}

if ($app -notmatch '--video-edit-v2-smoke' `
    -or $app -notmatch 'CaptureVideoEditV2Smoke') {
    throw 'The isolated AI video edit smoke dispatch is missing.'
}
if ($smoke -notmatch 'purePlanner' `
    -or $smoke -notmatch 'externalStartsUnverified' `
    -or $smoke -notmatch 'passiveOpen' `
    -or $smoke -notmatch 'pathResolverCallsAfterDrop' `
    -or $smoke -notmatch 'halfOpenPreview' `
    -or $smoke -notmatch 'malformedCompilerResponseRejected' `
    -or $smoke -notmatch 'candidateStales' `
    -or $smoke -notmatch 'autoSuppressed' `
    -or $smoke -notmatch 'escapeClosesBoard' `
    -or $smoke -notmatch 'minimizeClosesBoard' `
    -or $smoke -notmatch 'sourceNavigationClosesBoard' `
    -or $smoke -notmatch 'sourceUntouched') {
    throw 'The focused smoke does not cover pure planning, passive open, review, staleness, and source ownership.'
}

if ($StaticOnly) {
    [pscustomobject]@{
        ok = $true
        staticOnly = $true
        sourceFps = @(24, 30, 60)
        maximumSelectionSeconds = 5
        audioPolicies = @('preserve', 'mute')
        maximumPixelAreas = @(230400, 307200, 414720)
        writerEnabled = $false
    } | ConvertTo-Json -Depth 5
    return
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot (
    'aibos-wpf-video-edit-v2-' + [guid]::NewGuid().ToString('N'))))
$buildRoot = Join-Path $runRoot 'build'
$resultPath = Join-Path $runRoot 'result.json'
$stdoutPath = Join-Path $runRoot 'stdout.log'
$stderrPath = Join-Path $runRoot 'stderr.log'
$process = $null
$previousDotNetRoot = [Environment]::GetEnvironmentVariable(
    'DOTNET_ROOT',
    'Process')
$previousDotNetRootX64 = [Environment]::GetEnvironmentVariable(
    'DOTNET_ROOT_X64',
    'Process')

if (-not $runRoot.StartsWith(
        $tempPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Verifier root must stay under TEMP.'
}

try {
    New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    if ($DotNetPath -eq 'dotnet') {
        $localDotNet10 = Join-Path $env:LOCALAPPDATA (
            'Microsoft\dotnet10\dotnet.exe')
        if (Test-Path -LiteralPath $localDotNet10 -PathType Leaf) {
            $DotNetPath = $localDotNet10
        }
    }
    $dotNetExecutable = (Get-Command $DotNetPath -ErrorAction Stop).Source
    $dotNetRoot = Split-Path -Parent $dotNetExecutable
    [Environment]::SetEnvironmentVariable(
        'DOTNET_ROOT',
        $dotNetRoot,
        'Process')
    [Environment]::SetEnvironmentVariable(
        'DOTNET_ROOT_X64',
        $dotNetRoot,
        'Process')

    if (-not $SkipBuild) {
        $buildArguments = @('build', $projectPath, '-c', $Configuration)
        if ($NoRestore) {
            $output = $buildRoot.TrimEnd('\', '/') `
                + [IO.Path]::DirectorySeparatorChar
            $buildArguments += @("-p:OutputPath=$output", '--no-restore')
        }
        else {
            $buildArguments += @('--artifacts-path', $buildRoot)
        }
        $buildArguments += @('--nologo', '-v:minimal')
        & $dotNetExecutable @buildArguments
        if ($LASTEXITCODE -ne 0) {
            throw "WPF AI video edit verifier build failed with exit code $LASTEXITCODE."
        }
    }

    $dll = if ($SkipBuild) {
        Join-Path $repoRoot (
            'local-native\PhotoViewer.Wpf\bin\' + $Configuration +
            '\net10.0-windows\PhotoViewer.Wpf.dll')
    }
    else {
        Get-ChildItem -LiteralPath $buildRoot -Recurse `
            -Filter 'PhotoViewer.Wpf.dll' -ErrorAction Stop |
            Select-Object -First 1 -ExpandProperty FullName
    }
    if ([string]::IsNullOrWhiteSpace($dll) `
        -or -not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        throw 'Built WPF DLL was not found.'
    }

    $process = Start-Process `
        -FilePath $dotNetExecutable `
        -ArgumentList @(
            ('"{0}"' -f $dll),
            '--video-edit-v2-smoke',
            ('"{0}"' -f $resultPath)) `
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
        throw "WPF AI video edit smoke exceeded $OverallTimeoutSeconds seconds."
    }
    $process.WaitForExit()
    $process.Refresh()
    $processExitCode = [int]$process.ExitCode
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        $stderr = if (Test-Path -LiteralPath $stderrPath) {
            Get-Content -Raw -LiteralPath $stderrPath
        }
        else { 'no stderr' }
        throw "WPF AI video edit smoke did not produce a result. stderr=$stderr"
    }

    $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    $required = @(
        'purePlanner',
        'hiddenForImages',
        'videoEntry',
        'externalStartsUnverified',
        'passiveOpen',
        'exactProbeAccepted',
        'halfOpenPreview',
        'previewSeek',
        'malformedCompilerResponseRejected',
        'reviewWithoutStart',
        'candidateStales',
        'autoSuppressed',
        'transientOnly',
        'escapeClosesBoard',
        'minimizeClosesBoard',
        'sourceNavigationClosesBoard',
        'sourceChangeClosesStale',
        'sourceUntouched')
    $failed = @($required | Where-Object { $result.$_ -ne $true })
    if ($processExitCode -ne 0 `
        -or $result.ok -ne $true `
        -or $failed.Count -gt 0) {
        throw ('WPF AI video edit smoke failed: ' +
            ($result | ConvertTo-Json -Depth 8 -Compress) +
            '; processExitCode=' + $processExitCode +
            '; failed=' + ($failed -join ','))
    }
    $result | ConvertTo-Json -Depth 8
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath(
            (Resolve-Path -LiteralPath $runRoot).Path)
        if (-not $resolvedRunRoot.StartsWith(
                $tempPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to clean a verifier root outside TEMP.'
        }
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
    [Environment]::SetEnvironmentVariable(
        'DOTNET_ROOT',
        $previousDotNetRoot,
        'Process')
    [Environment]::SetEnvironmentVariable(
        'DOTNET_ROOT_X64',
        $previousDotNetRootX64,
        'Process')
}

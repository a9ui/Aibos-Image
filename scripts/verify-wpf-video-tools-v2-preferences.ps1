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
$projectPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$preferencesPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.VideoToolsV2Preferences.cs'
$smokePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\App.VideoToolsV2PreferencesSmoke.cs'
$localPersistencePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.LocalPersistence.cs'
$mainWindowPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.xaml.cs'
$xamlPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.xaml'
$appPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\App.xaml.cs'
$jaPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\Localization\StringResources.ja.xaml'
$enPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\Localization\StringResources.en.xaml'

foreach ($path in @(
    $projectPath,
    $preferencesPath,
    $smokePath,
    $localPersistencePath,
    $mainWindowPath,
    $xamlPath,
    $appPath,
    $jaPath,
    $enPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Video Tools v2 preferences source is missing: $path"
    }
}

$preferences = Get-Content -Raw -Encoding UTF8 -LiteralPath $preferencesPath
$smoke = Get-Content -Raw -Encoding UTF8 -LiteralPath $smokePath
$localPersistence = Get-Content -Raw -Encoding UTF8 -LiteralPath $localPersistencePath
$mainWindow = Get-Content -Raw -Encoding UTF8 -LiteralPath $mainWindowPath
$xaml = Get-Content -Raw -Encoding UTF8 -LiteralPath $xamlPath
$app = Get-Content -Raw -Encoding UTF8 -LiteralPath $appPath

foreach ($token in @(
    'VideoEditV2StyleState',
    'VideoToolsV2PreferenceState',
    'NormalizeVideoEditV2Style',
    'TryMutateVideoEditV2Styles',
    'ApplyVideoEditV2Style',
    'RestoreVideoToolsV2Preferences',
    'SnapshotVideoToolsV2Preferences')) {
    if (($preferences + $localPersistence + $mainWindow) -notmatch [regex]::Escape($token)) {
        throw "Video Tools v2 preferences are missing $token."
    }
}

foreach ($name in @(
    'SettingsVideoToolsV2Nav',
    'VideoToolsV2SettingsHeading',
    'AppVideoEditV2DefaultAudioComboBox',
    'AppVideoEditV2DefaultStrengthComboBox',
    'AppVideoEditV2DefaultCanvasComboBox',
    'AppVideoEditV2DefaultStepsSlider',
    'AppVideoEditV2DefaultSkipReviewCheckBox',
    'AppVideoFinishV2DefaultModeComboBox',
    'AppVideoFinishV2DefaultScaleComboBox',
    'AppVideoEditV2StyleListPanel',
    'ModalVideoEditV2SavedStyleComboBox',
    'ModalVideoEditV2StyleNameTextBox')) {
    if ($xaml -notmatch ('x:Name="' + [regex]::Escape($name) + '"')) {
        throw "Video Tools v2 settings are missing $name."
    }
}

$settingsMatch = [regex]::Match(
    $xaml,
    '<TextBlock x:Name="VideoToolsV2SettingsHeading"[\s\S]*?<TextBlock x:Name="UpscaleSettingsHeading"',
    [Text.RegularExpressions.RegexOptions]::CultureInvariant)
if (-not $settingsMatch.Success) {
    throw 'The dedicated Video Tools v2 settings section is missing.'
}
$finishDefaultsMatch = [regex]::Match(
    $settingsMatch.Value,
    '<ComboBox x:Name="AppVideoFinishV2DefaultModeComboBox"[\s\S]*?<ComboBox x:Name="AppVideoFinishV2DefaultScaleComboBox"[\s\S]*?</ComboBox>',
    [Text.RegularExpressions.RegexOptions]::CultureInvariant)
if (-not $finishDefaultsMatch.Success `
    -or $finishDefaultsMatch.Value -match 'x:Name="[^"]*(Prompt|Style|Step)[^"]*"' `
    -or $finishDefaultsMatch.Value -match 'NVIDIA|Bernini|MiniMax|SeedVR|NanoVSR') {
    throw 'Finish defaults leaked prompt, style, step, or backend controls.'
}

foreach ($token in @(
    'styleSaved',
    'styleReloaded',
    'styleApplied',
    'styleOverwritten',
    'styleDeleted',
    'compiledPromptNotSaved',
    'seedNotSaved',
    'candidateStale',
    'readinessStale',
    'malformedStyleNoWrite',
    'futureStyleNoWrite',
    'oversizedStyleNoWrite',
    'unknownFieldsPreserved',
    'defaultsReloaded',
    'passiveOpenNoWrite')) {
    if ($smoke -notmatch [regex]::Escape($token)) {
        throw "The focused preferences smoke is missing $token."
    }
}

if ($app -notmatch '--video-tools-v2-preferences-smoke' `
    -or $app -notmatch 'CaptureVideoToolsV2PreferencesSmoke') {
    throw 'The Video Tools v2 preferences smoke dispatch is missing.'
}

foreach ($resourcePath in @($jaPath, $enPath)) {
    [xml]$resource = Get-Content -Raw -Encoding UTF8 -LiteralPath $resourcePath
    $keys = @($resource.ResourceDictionary.String | ForEach-Object {
        $_.GetAttribute('Key', 'http://schemas.microsoft.com/winfx/2006/xaml')
    })
    foreach ($key in @(
        'UiVideoToolsV2Settings',
        'UiVideoEditV2SavedStyleLabel',
        'UiVideoEditV2StyleNameLabel',
        'UiVideoEditV2StyleSaved',
        'UiVideoEditV2StyleDeleted')) {
        if ($keys -notcontains $key) {
            throw "Missing localized Video Tools v2 preference key $key in $resourcePath"
        }
    }
}

if ($StaticOnly) {
    [pscustomobject]@{
        ok = $true
        staticOnly = $true
        editDefaults = 'preserve/balanced/high/20/review'
        finishDefaults = 'standard/2x'
        finishHasPromptStyleSteps = $false
    } | ConvertTo-Json -Depth 4
    return
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$runRoot = Join-Path $tempRoot ('aibos-wpf-video-tools-v2-preferences-' + [guid]::NewGuid().ToString('N'))
$buildRoot = Join-Path $runRoot 'build'
$resultPath = Join-Path $runRoot 'result.json'
$stdoutPath = Join-Path $runRoot 'stdout.log'
$stderrPath = Join-Path $runRoot 'stderr.log'
$process = $null

try {
    New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    if ($DotNetPath -eq 'dotnet') {
        $localDotNet10 = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet10\dotnet.exe'
        if (Test-Path -LiteralPath $localDotNet10 -PathType Leaf) {
            $DotNetPath = $localDotNet10
        }
    }
    if (-not $SkipBuild) {
        $buildArgs = @('build', $projectPath, '-c', $Configuration, '--artifacts-path', $buildRoot, '--nologo')
        if ($NoRestore) { $buildArgs += '--no-restore' }
        & $DotNetPath @buildArgs
        if ($LASTEXITCODE -ne 0) { throw "WPF build failed with exit code $LASTEXITCODE" }
    }
    $appDll = if ($SkipBuild) {
        Get-Item -LiteralPath (Join-Path $repoRoot (
            "local-native\PhotoViewer.Wpf\bin\$Configuration\net10.0-windows\PhotoViewer.Wpf.dll")) -ErrorAction SilentlyContinue
    }
    else {
        Get-ChildItem -LiteralPath $buildRoot -Recurse -Filter 'PhotoViewer.Wpf.dll' |
            Where-Object FullName -Match '\\bin\\PhotoViewer.Wpf\\release_' |
            Select-Object -First 1
    }
    if ($null -eq $appDll) { throw 'Built PhotoViewer.Wpf.dll was not found.' }
    $process = Start-Process -FilePath $DotNetPath -ArgumentList @(
        $appDll.FullName,
        '--video-tools-v2-preferences-smoke',
        $resultPath) -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
    if (-not $process.WaitForExit($OverallTimeoutSeconds * 1000)) {
        $process.Kill($true)
        throw 'Video Tools v2 preferences smoke timed out.'
    }
    if ($process.ExitCode -ne 0) {
        throw "Video Tools v2 preferences smoke failed with exit code $($process.ExitCode)."
    }
    $result = Get-Content -Raw -Encoding UTF8 -LiteralPath $resultPath | ConvertFrom-Json
    if ($result.ok -ne $true) { throw "Video Tools v2 preferences smoke failed: $($result.message)" }
    $result | ConvertTo-Json -Depth 8
}
finally {
    if ($null -ne $process -and -not $process.HasExited) { $process.Kill($true) }
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        if ($resolvedRunRoot.StartsWith($tempRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
        }
    }
}

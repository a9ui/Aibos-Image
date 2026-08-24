[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$DotNetPath = 'dotnet',
    [string]$PairedJobPath,
    [switch]$SkipBuild,
    [switch]$StaticOnly,
    [switch]$NoRestore,
    [ValidateRange(30, 180)]
    [int]$OverallTimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$appPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\App.xaml.cs'
$smokePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\App.VideoTrimV1Smoke.cs'
$contractCodePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\VideoTrimV1Contract.cs'
$surfacePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.VideoTrimV1.cs'
$durablePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.VideoTrimV1Durable.cs'
$readerPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.VideoTrimV1Reader.cs'
$inventoryPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.VideoTrimV1Inventory.cs'
$jobsPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.EnhancementJobs.cs'
$xamlPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.xaml'
$jaPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\Localization\StringResources.ja.xaml'
$enPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\Localization\StringResources.en.xaml'
$contractPath = Join-Path $repoRoot 'contracts\enhancement-video-trim-v1.json'
$fixturePath = Join-Path $repoRoot 'contracts\fixtures\enhancement-video-trim-v1-reader-v1.json'
$contractVerifierPath = Join-Path $repoRoot 'scripts\verify-video-trim-v1-contract.ps1'

$required = @(
    $projectPath, $appPath, $smokePath, $contractCodePath, $surfacePath,
    $durablePath, $readerPath, $inventoryPath, $jobsPath, $xamlPath,
    $jaPath, $enPath, $contractPath, $fixturePath, $contractVerifierPath)
foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Video Trim v1 input is missing: $path"
    }
}

$expectedContractSha = 'f0e2b48c06c78a04ec64cb70bd36f03c8ed1f65a89ce31551cd34e225bfe3c40'
$expectedFixtureSha = '65d74368293fff2a65d730ad1fa1d2cd3f32a200179ab0eaa6db738991582ec8'
$actualContractSha = (Get-FileHash -LiteralPath $contractPath -Algorithm SHA256).Hash.ToLowerInvariant()
$actualFixtureSha = (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualContractSha -cne $expectedContractSha -or
    $actualFixtureSha -cne $expectedFixtureSha) {
    throw "Video Trim v1 frozen SHA mismatch. contract=$actualContractSha fixture=$actualFixtureSha"
}

& $contractVerifierPath | Out-Null
if (-not $?) {
    throw 'Video Trim v1 contract verifier failed.'
}

$app = Get-Content -Raw -Encoding UTF8 -LiteralPath $appPath
$smoke = Get-Content -Raw -Encoding UTF8 -LiteralPath $smokePath
$contractCode = Get-Content -Raw -Encoding UTF8 -LiteralPath $contractCodePath
$surface = Get-Content -Raw -Encoding UTF8 -LiteralPath $surfacePath
$durable = Get-Content -Raw -Encoding UTF8 -LiteralPath $durablePath
$reader = Get-Content -Raw -Encoding UTF8 -LiteralPath $readerPath
$inventory = Get-Content -Raw -Encoding UTF8 -LiteralPath $inventoryPath
$jobs = Get-Content -Raw -Encoding UTF8 -LiteralPath $jobsPath
$xaml = Get-Content -Raw -Encoding UTF8 -LiteralPath $xamlPath
$resources = (Get-Content -Raw -Encoding UTF8 -LiteralPath $jaPath) +
    (Get-Content -Raw -Encoding UTF8 -LiteralPath $enPath)

if ($app -notmatch '--video-trim-v1-smoke' -or
    $app -notmatch 'CaptureVideoTrimV1Smoke') {
    throw 'The focused Video Trim v1 WPF dispatch is missing.'
}

foreach ($token in @(
    'PV-ENHANCE-VIDEO-TRIM-001',
    'aibos-enhancement-video-trim-v1',
    'api/enhance/video-trim/v1/source-inspection',
    'MaximumSourceBytes = 536_870_912',
    'MaximumDurationMs = 300_000',
    'MaximumFrames = 18_000',
    'startFrame',
    'endFrameExclusive',
    'audioPolicy',
    'videoTrim')) {
    if ($contractCode -notmatch [regex]::Escape($token)) {
        throw "The Video Trim v1 WPF contract is missing $token."
    }
}
if ($contractCode -match 'videoTools[\s\S]{0,200}kind[\s\S]{0,100}trim') {
    throw 'Video Trim v1 must not be encoded as Video Tools v2 kind=trim.'
}

foreach ($token in @(
    'ModalVideoTrimV1Button',
    'ModalContextVideoTrimV1',
    'ModalVideoTrimV1ExternalMenuItem',
    'ModalVideoTrimV1Popup',
    'ModalVideoTrimV1OverviewProgressBar',
    'ModalVideoTrimV1CurrentFrameText',
    'ModalVideoTrimV1StartFrameTextBox',
    'ModalVideoTrimV1EndFrameTextBox',
    'ModalVideoTrimV1StartMinusButton',
    'ModalVideoTrimV1StartPlusButton',
    'ModalVideoTrimV1EndMinusButton',
    'ModalVideoTrimV1EndPlusButton',
    'ModalVideoTrimV1StartPreviewImage',
    'ModalVideoTrimV1MiddlePreviewImage',
    'ModalVideoTrimV1EndPreviewImage',
    'ModalVideoTrimV1AudioComboBox',
    'ModalVideoTrimV1ReadinessButton',
    'ModalVideoTrimV1StartButton')) {
    if ($xaml -notmatch [regex]::Escape($token)) {
        throw "The independent Video Trim v1 surface is missing $token."
    }
}
$trimBoard = [regex]::Match(
    $xaml,
    '<Grid x:Name="ModalVideoTrimV1Popup"[\s\S]*?<Grid x:Name="ModalVideoEditV2Popup"',
    [Text.RegularExpressions.RegexOptions]::CultureInvariant)
if (-not $trimBoard.Success) {
    throw 'The independent Video Trim v1 board boundary is missing.'
}
if ($trimBoard.Value -match 'x:Name="[^"]*(Prompt|Style|Step|Seed)[^"]*"') {
    throw 'Video Trim v1 must not contain Prompt, Style, STEP, or Seed controls.'
}

foreach ($token in @(
    'BuildProbeRequestJson',
    'BuildPreviewRequestJson',
    'SendEnhancementApiAsync',
    'SendPassiveEnhancementReadAsync',
    'SendEnhancementEnqueueAsync',
    'requireExactHealthValidation: true',
    'onBeforeDurablePublish',
    'VideoTrimV1Contract.IsExactReadyHealth')) {
    if (($surface + $durable) -notmatch [regex]::Escape($token)) {
        throw "The explicit Video Trim v1 flow is missing $token."
    }
}
$openMethod = [regex]::Match(
    $surface,
    'private void OpenModalVideoTrimV1Board\(\)[\s\S]*?private void CloseModalVideoTrimV1_Click',
    [Text.RegularExpressions.RegexOptions]::CultureInvariant)
if (-not $openMethod.Success -or
    $openMethod.Value -match 'SendEnhancement|HttpMethod|LoadModalVideoTrimV1FramesAsync') {
    throw 'Opening Video Trim v1 must remain passive.'
}

foreach ($token in @(
    'ClaimsVideoTrimV1WorkspaceSnapshot',
    'IsVideoTrimReaderOnly',
    'IsExactCurrentVideoTrimV1',
    'CanUseVideoTrimV1Output',
    'VideoKindFilterKey',
    '"trim"',
    'TryBuildVideoTrimV1ManagedVideoVersion',
    '"動画トリム"')) {
    if (($reader + $inventory + $jobs) -notmatch [regex]::Escape($token)) {
        throw "Video Trim v1 Jobs/inventory wiring is missing $token."
    }
}

foreach ($token in @(
    'DropExternalVideoForSmokeAsync',
    'OpenVideoTrimV1ForSmoke',
    'LoadVideoTrimV1FramesForSmokeAsync',
    'RefreshVideoTrimV1ReadinessForSmokeAsync',
    'StartVideoTrimV1ForSmokeAsync',
    'OpenEnhancementJobsForSmokeAsync',
    'SelectEnhancementJobsVideoKindFilterForSmoke("trim")',
    'CancelEnhancementJobForSmokeAsync',
    'RetryEnhancementJobForSmokeAsync',
    'DismissEnhancementJobForSmokeAsync',
    'DeleteEnhancementJobOutputForSmokeAsync',
    'ResolveVideoToolsV2ManagedInventoryForSmoke',
    'SelectModalVideoJobForSmoke',
    'expandedOutputExact',
    'readerExact',
    'passiveOpen',
    'passiveJobs',
    'sourceUntouched')) {
    if ($smoke -notmatch [regex]::Escape($token)) {
        throw "The focused Video Trim v1 smoke is missing $token."
    }
}
if ($smoke -match 'Process\.Start|Start-Process|Desktop\\Tools|AibosImage-Companion') {
    throw 'The focused Video Trim v1 smoke must not launch a live Companion or embed a private path.'
}
if ($resources -match 'remux runtime|remuxで') {
    throw 'The Video Trim v1 UI must describe exact re-encoding, not remux.'
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$pairedJobFullPath = $null
if (-not [string]::IsNullOrWhiteSpace($PairedJobPath)) {
    if (-not (Test-Path -LiteralPath $PairedJobPath -PathType Leaf)) {
        throw "The paired Video Trim Job is missing: $PairedJobPath"
    }
    $pairedJobFullPath = [IO.Path]::GetFullPath(
        (Resolve-Path -LiteralPath $PairedJobPath).Path)
    if (-not $pairedJobFullPath.StartsWith(
            $tempRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The paired Video Trim Job must be a leaf under the system TEMP root.'
    }
    $pairedJobLength = (Get-Item -LiteralPath $pairedJobFullPath).Length
    if ($pairedJobLength -gt 1MB) {
        throw 'The paired Video Trim Job exceeds the 1 MiB shared JSON limit.'
    }
}

if ($StaticOnly) {
    [pscustomobject]@{
        ok = $true
        staticOnly = $true
        contractSha256 = $actualContractSha
        fixtureSha256 = $actualFixtureSha
        passiveOpen = $true
        independentBoard = $true
        readerOnlyProtection = $true
        isolatedTemp = $true
        liveCompanion = $false
    } | ConvertTo-Json -Depth 4
    return
}

$runRoot = Join-Path $tempRoot ('aibos-wpf-video-trim-v1-' + [guid]::NewGuid().ToString('N'))
$buildRoot = Join-Path $runRoot 'build'
$resultPath = Join-Path $runRoot 'result.json'
$stdoutPath = Join-Path $runRoot 'stdout.log'
$stderrPath = Join-Path $runRoot 'stderr.log'
$process = $null
$previousDotNetRoot = [Environment]::GetEnvironmentVariable('DOTNET_ROOT', 'Process')
$previousDotNetRootX64 = [Environment]::GetEnvironmentVariable('DOTNET_ROOT_X64', 'Process')

try {
    New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    $dotNetExecutable = (Get-Command $DotNetPath -ErrorAction Stop).Source
    $dotNetRoot = Split-Path -Parent $dotNetExecutable
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $dotNetRoot, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT_X64', $dotNetRoot, 'Process')

    if (-not $SkipBuild) {
        $buildArgs = @(
            'build', $projectPath, '-c', $Configuration,
            '--artifacts-path', $buildRoot, '--nologo', '-v:minimal')
        if ($NoRestore) { $buildArgs += '--no-restore' }
        & $dotNetExecutable @buildArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Video Trim v1 build failed with exit code $LASTEXITCODE."
        }
    }

    $appDll = if ($SkipBuild) {
        Get-Item -LiteralPath (Join-Path $repoRoot (
            "local-native\PhotoViewer.Wpf\bin\$Configuration\net10.0-windows\PhotoViewer.Wpf.dll")) -ErrorAction SilentlyContinue
    }
    else {
        Get-ChildItem -LiteralPath $buildRoot -Recurse -Filter 'PhotoViewer.Wpf.dll' |
            Where-Object FullName -Match '\\bin\\PhotoViewer.Wpf\\release(?:_|\\)' |
            Select-Object -First 1
    }
    if ($null -eq $appDll) { throw 'Built PhotoViewer.Wpf.dll was not found.' }

    $smokeArguments = @(
        $appDll.FullName,
        '--video-trim-v1-smoke',
        $resultPath,
        '--fixture',
        $fixturePath)
    if ($null -ne $pairedJobFullPath) {
        $smokeArguments += @('--paired-job', $pairedJobFullPath)
    }
    $process = Start-Process -FilePath $dotNetExecutable -ArgumentList $smokeArguments -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
    if (-not $process.WaitForExit($OverallTimeoutSeconds * 1000)) {
        $process.Kill($true)
        throw 'Video Trim v1 smoke timed out.'
    }
    $process.WaitForExit()
    $process.Refresh()
    if ($process.ExitCode -ne 0) {
        $detail = if (Test-Path -LiteralPath $resultPath) {
            Get-Content -Raw -Encoding UTF8 -LiteralPath $resultPath
        }
        else {
            Get-Content -Raw -Encoding UTF8 -LiteralPath $stderrPath
        }
        throw "Video Trim v1 smoke failed with exit code $($process.ExitCode): $detail"
    }
    $result = Get-Content -Raw -Encoding UTF8 -LiteralPath $resultPath | ConvertFrom-Json
    if ($result.ok -ne $true) {
        throw "Video Trim v1 smoke returned a failing result: $($result | ConvertTo-Json -Depth 10 -Compress)"
    }
    if ($null -ne $pairedJobFullPath -and
        ($result.pairedJobChecked -ne $true -or
            $result.pairedJobExact -ne $true)) {
        throw 'The paired Video Trim Job did not pass the exact WPF reader regression.'
    }
    $result | ConvertTo-Json -Depth 10
}
finally {
    if ($null -ne $process -and -not $process.HasExited) { $process.Kill($true) }
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $previousDotNetRoot, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT_X64', $previousDotNetRootX64, 'Process')
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        if ($resolvedRunRoot.StartsWith(
                $tempRoot + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
        }
    }
}

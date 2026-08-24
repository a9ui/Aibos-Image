[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$DotNetPath = 'dotnet',
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
$smokePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\App.VideoToolsV2FlowSmoke.cs'
$xamlPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.xaml'
$fixturePath = Join-Path $repoRoot 'contracts\fixtures\enhancement-video-tools-v2-reader-v1.json'

foreach ($path in @($projectPath, $appPath, $smokePath, $xamlPath, $fixturePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Video Tools v2 flow input is missing: $path"
    }
}

$app = Get-Content -Raw -Encoding UTF8 -LiteralPath $appPath
$smoke = Get-Content -Raw -Encoding UTF8 -LiteralPath $smokePath
$xaml = Get-Content -Raw -Encoding UTF8 -LiteralPath $xamlPath
if ($app -notmatch '--video-tools-v2-flow-smoke' `
    -or $app -notmatch 'CaptureVideoToolsV2FlowSmoke') {
    throw 'The focused Video Tools v2 flow dispatch is missing.'
}

foreach ($token in @(
    'DropExternalVideoForSmokeAsync',
    'OpenVideoEditV2ForSmoke',
    'LoadVideoEditV2FramesForSmokeAsync',
    'CompileVideoEditV2ForSmokeAsync',
    'ModalVideoEditV2StartButton.RaiseEvent',
    'OpenVideoFinishV2ForSmoke',
    'StartVideoFinishV2ForSmokeAsync',
    'SendIdempotentEnhancementMutationForSmokeAsync',
    'OpenEnhancementJobsForSmokeAsync',
    'CancelEnhancementJobForSmokeAsync',
    'RetryEnhancementJobForSmokeAsync',
    'DismissEnhancementJobForSmokeAsync',
    'DeleteEnhancementJobOutputForSmokeAsync',
    'ResolveVideoToolsV2ManagedInventoryForSmoke',
    'VideoEditV2CandidateStaleForStyleSmoke',
    'futureReadOnly',
    'sourceUntouched',
    'qualityNoFallback',
    'passiveJobs')) {
    if ($smoke -notmatch [regex]::Escape($token)) {
        throw "The focused Video Tools v2 flow is missing $token."
    }
}
if ($smoke -match 'Process\.Start|Start-Process|Desktop\\Tools|AibosImage-Companion') {
    throw 'The focused flow must not launch a live Companion or embed a private path.'
}

$editEntry = [regex]::Match(
    $xaml,
    'x:Name="ModalVideoEditV2Button"[\s\S]{0,600}?Click="OpenModalVideoEditV2_Click"',
    [Text.RegularExpressions.RegexOptions]::CultureInvariant)
$finishEntry = [regex]::Match(
    $xaml,
    'x:Name="ModalVideoFinishButton"[\s\S]{0,600}?Click="OpenModalVideoFinish_Click"',
    [Text.RegularExpressions.RegexOptions]::CultureInvariant)
if (-not $editEntry.Success -or -not $finishEntry.Success) {
    throw 'Edit and Finish must remain separate enlarged-video entries.'
}
$finishBoard = [regex]::Match(
    $xaml,
    '<Grid x:Name="ModalVideoFinishV2Popup"[\s\S]*?<Grid x:Name="ModalVideoEditV2Popup"',
    [Text.RegularExpressions.RegexOptions]::CultureInvariant)
if (-not $finishBoard.Success `
    -or $finishBoard.Value -match 'x:Name="[^"]*(Prompt|Style|Step)[^"]*"') {
    throw 'Finish must have its own board without Prompt, Style, or STEP controls.'
}

if ($StaticOnly) {
    [pscustomobject]@{
        ok = $true
        staticOnly = $true
        isolatedTemp = $true
        liveCompanion = $false
        editAndFinishIndependent = $true
    } | ConvertTo-Json -Depth 4
    return
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$runRoot = Join-Path $tempRoot ('aibos-wpf-video-tools-v2-flow-' + [guid]::NewGuid().ToString('N'))
$buildRoot = Join-Path $runRoot 'build'
$resultPath = Join-Path $runRoot 'result.json'
$stdoutPath = Join-Path $runRoot 'stdout.log'
$stderrPath = Join-Path $runRoot 'stderr.log'
$process = $null
$previousDotNetRoot = [Environment]::GetEnvironmentVariable('DOTNET_ROOT', 'Process')
$previousDotNetRootX64 = [Environment]::GetEnvironmentVariable('DOTNET_ROOT_X64', 'Process')

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
        $buildArgs = @(
            'build',
            $projectPath,
            '-c',
            $Configuration,
            '--artifacts-path',
            $buildRoot,
            '--nologo',
            '-v:minimal')
        if ($NoRestore) { $buildArgs += '--no-restore' }
        & $dotNetExecutable @buildArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Video Tools v2 flow build failed with exit code $LASTEXITCODE."
        }
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

    $process = Start-Process -FilePath $dotNetExecutable -ArgumentList @(
        $appDll.FullName,
        '--video-tools-v2-flow-smoke',
        $resultPath,
        '--fixture',
        $fixturePath) -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
    if (-not $process.WaitForExit($OverallTimeoutSeconds * 1000)) {
        $process.Kill($true)
        throw 'Video Tools v2 flow smoke timed out.'
    }
    $process.WaitForExit()
    $process.Refresh()
    $flowExitCode = $process.ExitCode
    if ($null -ne $flowExitCode -and $flowExitCode -ne 0) {
        $detail = if (Test-Path -LiteralPath $resultPath) {
            Get-Content -Raw -Encoding UTF8 -LiteralPath $resultPath
        }
        else {
            Get-Content -Raw -Encoding UTF8 -LiteralPath $stderrPath
        }
        throw "Video Tools v2 flow smoke failed with exit code ${flowExitCode}: $detail"
    }
    $result = Get-Content -Raw -Encoding UTF8 -LiteralPath $resultPath | ConvertFrom-Json
    if ($result.ok -ne $true) {
        throw "Video Tools v2 flow smoke returned a failing result."
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

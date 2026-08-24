param(
    [string]$Configuration = 'Release',
    [string]$DotNetPath = 'dotnet',
    [switch]$SkipBuild,
    [switch]$StaticOnly,
    [switch]$NoRestore,
    [string]$PairedJobsPath = '',
    [ValidateRange(10, 120)]
    [int]$OverallTimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$readerPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.VideoToolsV2Reader.cs'
$v1ReaderPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.VideoTools.cs'
$smokePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\App.VideoToolsV2ReaderSmoke.cs'
$jobsPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.EnhancementJobs.cs'
$xamlPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.xaml'
$appPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\App.xaml.cs'
$projectPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$fixturePath = Join-Path $repoRoot 'contracts\fixtures\enhancement-video-tools-v2-reader-v1.json'
$v1FixturePath = Join-Path $repoRoot 'contracts\enhancement-video-tools-v1.json'
$jaResourcePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\Localization\StringResources.ja.xaml'
$enResourcePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\Localization\StringResources.en.xaml'

foreach ($requiredPath in @(
    $readerPath,
    $v1ReaderPath,
    $smokePath,
    $jobsPath,
    $xamlPath,
    $appPath,
    $projectPath,
    $fixturePath,
    $v1FixturePath,
    $jaResourcePath,
    $enResourcePath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Video Tools v2 reader source is missing: $requiredPath"
    }
}

$fixtureHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $fixturePath).Hash.ToLowerInvariant()
if ($fixtureHash -cne 'a6e307d401d911e0e0bdc6e4296bc6f95d6a76b8b3a25be11dee2012e7a19e46') {
    throw "Unexpected Video Tools v2 fixture hash: $fixtureHash"
}

$reader = Get-Content -Raw -Encoding UTF8 -LiteralPath $readerPath
$v1Reader = Get-Content -Raw -Encoding UTF8 -LiteralPath $v1ReaderPath
$smoke = Get-Content -Raw -Encoding UTF8 -LiteralPath $smokePath
$jobs = Get-Content -Raw -Encoding UTF8 -LiteralPath $jobsPath
$xaml = Get-Content -Raw -Encoding UTF8 -LiteralPath $xamlPath
$app = Get-Content -Raw -Encoding UTF8 -LiteralPath $appPath

if ($reader -match 'HttpClient|SendEnhancement|Enqueue|SaveState|File\.|Directory\.|Process\.|Task\.Run' `
    -or $reader -notmatch 'aibos-enhancement-video-tools-v2' `
    -or $reader -notmatch 'TryReadVideoToolsV2WorkspaceSnapshot' `
    -or $reader -notmatch 'HasExactProperties' `
    -or $reader -notmatch 'HashStableJson' `
    -or $reader -notmatch 'sourceVideoJobId' `
    -or $reader -notmatch 'childClip' `
    -or $reader -notmatch 'preserveSourceAudioPackets') {
    throw 'The pure Video Tools v2 reader is missing exact closed-shape or passive-read guards.'
}
if ($v1Reader -notmatch 'jobClaimsVideoToolsV2' `
    -or $v1Reader -notmatch 'schemaAndKindClaimVideoToolsV2' `
    -or $v1Reader -notmatch 'EnumerateObject\(\)\.Any') {
    throw 'The broad Video Tools claim guard must protect malformed or duplicate v2 envelopes.'
}
if ($jobs -notmatch '_enhancementWorkspaceVideoKindFilter' `
    -or $jobs -notmatch 'MatchesEnhancementWorkspaceVideoKindFilter' `
    -or $jobs -notmatch 'EnhancementJobsVideoKindFilter_Click' `
    -or $jobs -notmatch 'VideoKindFilterKey' `
    -or $jobs -notmatch 'VideoToolsV2Snapshot') {
    throw 'Jobs does not carry the additive Video Tools v2 kind classification.'
}
foreach ($tag in @('all', 'generation', 'edit', 'finish')) {
    if ($xaml -notmatch ('EnhancementJobsVideoKindFiltersPanel[\s\S]*?Tag="' + [regex]::Escape($tag) + '"')) {
        throw "The video kind filter is missing tag $tag."
    }
}
if ($app -notmatch '--video-tools-v2-reader-smoke' `
    -or $app -notmatch 'CaptureVideoToolsV2ReaderSmoke') {
    throw 'The focused Video Tools v2 reader smoke dispatch is missing.'
}
foreach ($token in @(
    'editPresetHashExact',
    'editRendererExact',
    'nestedExtraRejected',
    'duplicateRejected',
    'missingRejected',
    'semanticForgeryRejected',
    'lexicalPathIdentityProtected',
    'ecmaTrimExact',
    'numericIntegerFormsAccepted',
    'audioProbeBoundsExact',
    'editPlanBoundsExact',
    'futureProtected',
    'v1MeaningPreserved',
    'kindFiltersExact',
    'editLifecycle',
    'finishLifecycle',
    'knownLifecycleEnabled',
    'exactLifecycleProtection',
    'fixtureLifecycleVectorsExact',
    'pairedPrivateJobsExact',
    'pairedPrivateSqliteJobsExact',
    'lifecyclePresentationExact',
    'existingLifecycleRegression',
    'passiveRead')) {
    if ($smoke -notmatch [regex]::Escape($token)) {
        throw "The focused reader smoke is missing $token."
    }
}

$requiredResourceKeys = @(
    'UiJobsVideoKindTitle',
    'UiJobsVideoKindAll',
    'UiJobsVideoKindGeneration',
    'UiJobsVideoKindEdit',
    'UiJobsVideoKindFinish',
    'UiJobsVideoKindAllHelp',
    'UiJobsVideoKindGenerationHelp',
    'UiJobsVideoKindEditHelp',
    'UiJobsVideoKindFinishHelp')
foreach ($resourcePath in @($jaResourcePath, $enResourcePath)) {
    [xml]$resource = Get-Content -Raw -Encoding UTF8 -LiteralPath $resourcePath
    $keys = @($resource.ResourceDictionary.String | ForEach-Object {
        $_.GetAttribute(
            'Key',
            'http://schemas.microsoft.com/winfx/2006/xaml')
    })
    foreach ($requiredKey in $requiredResourceKeys) {
        if ($keys -notcontains $requiredKey) {
            throw "Missing localized Jobs video-kind resource $requiredKey in $resourcePath"
        }
    }
}

if ($StaticOnly) {
    [pscustomobject]@{
        ok = $true
        staticOnly = $true
        operation = 'video'
        kinds = @('generation', 'edit', 'finish')
        writerEnabled = $false
        openOutputEnabled = $true
    } | ConvertTo-Json -Depth 5
    return
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$resolvedPairedJobsPath = $null
if (-not [string]::IsNullOrWhiteSpace($PairedJobsPath)) {
    $resolvedPairedJobsPath = [IO.Path]::GetFullPath($PairedJobsPath)
    if (-not $resolvedPairedJobsPath.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) `
        -or -not (Test-Path -LiteralPath $resolvedPairedJobsPath -PathType Leaf)) {
        throw 'Paired Video Tools v2 Job bridge must be one existing TEMP file.'
    }
}
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot (
    'aibos-wpf-video-tools-v2-reader-' + [guid]::NewGuid().ToString('N'))))
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
        $buildArguments = @('build', $projectPath, '-c', $Configuration)
        if ($NoRestore) {
            $output = $buildRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
            $buildArguments += @("-p:OutputPath=$output", '--no-restore')
        }
        else {
            $buildArguments += @('--artifacts-path', $buildRoot)
        }
        $buildArguments += @('--nologo', '-v:minimal')
        & $dotNetExecutable @buildArguments
        if ($LASTEXITCODE -ne 0) {
            throw "WPF Video Tools v2 reader build failed with exit code $LASTEXITCODE."
        }
    }

    $dll = if ($SkipBuild) {
        Join-Path $repoRoot ('local-native\PhotoViewer.Wpf\bin\' + $Configuration + '\net10.0-windows\PhotoViewer.Wpf.dll')
    }
    else {
        Get-ChildItem -LiteralPath $buildRoot -Recurse -Filter 'PhotoViewer.Wpf.dll' -ErrorAction Stop |
            Select-Object -First 1 -ExpandProperty FullName
    }
    if ([string]::IsNullOrWhiteSpace($dll) -or -not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        throw 'Built WPF DLL was not found.'
    }

    $smokeArguments = @(
        ('"{0}"' -f $dll),
        '--video-tools-v2-reader-smoke',
        ('"{0}"' -f $resultPath),
        '--fixture',
        ('"{0}"' -f $fixturePath),
        '--v1-fixture',
        ('"{0}"' -f $v1FixturePath))
    if ($resolvedPairedJobsPath) {
        $smokeArguments += @(
            '--paired-jobs',
            ('"{0}"' -f $resolvedPairedJobsPath))
    }
    $process = Start-Process `
        -FilePath $dotNetExecutable `
        -ArgumentList $smokeArguments `
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
        throw "WPF Video Tools v2 reader smoke exceeded $OverallTimeoutSeconds seconds."
    }
    $process.WaitForExit()
    $process.Refresh()
    $processExitCode = [int]$process.ExitCode
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        $stderr = if (Test-Path -LiteralPath $stderrPath) {
            Get-Content -Raw -LiteralPath $stderrPath
        }
        else { 'no stderr' }
        throw "Reader smoke did not produce a result. stderr=$stderr"
    }
    $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    $required = @(
        'exactEdit',
        'exactFinish',
        'detailsExact',
        'nestedExtraRejected',
        'duplicateRejected',
        'missingRejected',
        'semanticForgeryRejected',
        'lexicalPathIdentityProtected',
        'ecmaTrimExact',
        'numericIntegerFormsAccepted',
        'audioProbeBoundsExact',
        'editPlanBoundsExact',
        'futureProtected',
        'v1MeaningPreserved',
        'kindFiltersExact',
        'editLifecycle',
        'finishLifecycle',
        'knownLifecycleEnabled',
        'exactLifecycleProtection',
        'fixtureLifecycleVectorsExact',
        'lifecyclePresentationExact',
        'existingLifecycleRegression',
        'passiveRead')
    if ($resolvedPairedJobsPath) {
        $required += @(
            'pairedPrivateJobsExact',
            'pairedPrivateSqliteJobsExact'
        )
    }
    $failed = @($required | Where-Object { $result.$_ -ne $true })
    if ($processExitCode -ne 0 -or $result.ok -ne $true -or $failed.Count -gt 0) {
        throw ('WPF Video Tools v2 reader smoke failed: ' +
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
        $resolvedRunRoot = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $runRoot).Path)
        if (-not $resolvedRunRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to clean a verifier root outside TEMP.'
        }
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $previousDotNetRoot, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT_X64', $previousDotNetRootX64, 'Process')
}

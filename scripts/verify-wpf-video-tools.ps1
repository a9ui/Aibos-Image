param(
    [string]$Configuration = 'Release',
    [string]$DotNetPath = 'dotnet',
    [switch]$SkipBuild,
    [switch]$StaticOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$xamlPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.xaml'
$implementationPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.VideoTools.cs'
$jobsPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.EnhancementJobs.cs'
$smokePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\App.VideoToolsSmoke.cs'
$appPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\App.xaml.cs'
$contractPath = Join-Path $repoRoot 'contracts\enhancement-video-tools-v1.json'
$jaResourcePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\Localization\StringResources.ja.xaml'
$enResourcePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\Localization\StringResources.en.xaml'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('aibos-wpf-video-tools-' + [guid]::NewGuid().ToString('N'))))
$runParent = [IO.Path]::GetDirectoryName($runRoot)
$runLeaf = [IO.Path]::GetFileName($runRoot)
if (-not [string]::Equals($runParent, $tempRoot, [StringComparison]::OrdinalIgnoreCase) `
    -or $runLeaf -notmatch '^aibos-wpf-video-tools-[0-9a-f]{32}$') {
    throw "Run root escaped the exact TEMP boundary: $runRoot"
}

$buildRoot = Join-Path $runRoot 'build'
$storesRoot = Join-Path $runRoot 'stores'
$resultPath = Join-Path $runRoot 'result.json'
$stdoutPath = Join-Path $runRoot 'stdout.log'
$stderrPath = Join-Path $runRoot 'stderr.log'
$previousEnvironment = @{}
$environmentPaths = [ordered]@{
    PHOTOVIEWER_WPF_STATE_PATH = (Join-Path $storesRoot 'state.json')
    PHOTOVIEWER_WPF_FAVORITES_PATH = (Join-Path $storesRoot 'favorites.json')
    PHOTOVIEWER_WPF_SEEN_PATH = (Join-Path $storesRoot 'seen.json')
    PHOTOVIEWER_WPF_RECENT_PATH = (Join-Path $storesRoot 'recent-folders.json')
    PHOTOVIEWER_WPF_ALBUMS_PATH = (Join-Path $storesRoot 'albums.json')
    PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH = (Join-Path $storesRoot 'search-history.json')
    PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH = (Join-Path $storesRoot 'enhance\jobs.json')
    PHOTOVIEWER_WPF_ENHANCEMENT_OUTPUT_ROOT = (Join-Path $storesRoot 'outputs')
    AIBOS_VIDEO_TOOLS_CONTRACT_PATH = $contractPath
}

try {
    New-Item -ItemType Directory -Path $runRoot, $storesRoot -Force | Out-Null

    if (-not (Test-Path -LiteralPath $contractPath -PathType Leaf)) {
        throw "Video Tools contract was not found: $contractPath"
    }
    $contractHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $contractPath).Hash
    if ($contractHash -ne '7CBB3A7D6963A2FA8BC2C79EA6E12CC5E9425480EEAC9B7CED38A80470D97EA7') {
        throw "Video Tools paired contract hash is not exact: $contractHash"
    }
    $contract = Get-Content -Raw -Encoding UTF8 -LiteralPath $contractPath |
        ConvertFrom-Json
    if ($contract.contractId -ne 'PV-ENHANCE-VIDEO-TOOLS-001' `
        -or $contract.protocol -ne 'aibos-enhancement-video-tools-v1' `
        -or $contract.readerFixtures.retake.job.presetHash -ne '3b62213062d0' `
        -or $contract.readerFixtures.finish.job.presetHash -ne '6467a0f6a2d8' `
        -or $contract.persistedSnapshot.unknownOrMalformed -notmatch 'reader-only protected') {
        throw 'The exact Video Tools reader fixtures or protection rule is missing.'
    }

    $xaml = Get-Content -Raw -Encoding UTF8 -LiteralPath $xamlPath
    if ($xaml -notmatch 'x:Name="ModalVideoRetakeButton"[\s\S]{0,500}Visibility="Collapsed"[\s\S]{0,100}OpenModalVideoRetake_Click' `
        -or $xaml -notmatch 'x:Name="ModalVideoFinishButton"[\s\S]{0,500}Visibility="Collapsed"[\s\S]{0,100}OpenModalVideoFinish_Click' `
        -or $xaml -notmatch 'x:Name="ModalVideoToolsPopup"[\s\S]{0,100}Visibility="Collapsed"' `
        -or $xaml -notmatch 'x:Name="VideoToolsSelectionStartTextBox"' `
        -or $xaml -notmatch 'x:Name="VideoToolsSelectionEndTextBox"' `
        -or $xaml -notmatch 'x:Name="VideoToolsRetakePlanText"' `
        -or $xaml -notmatch 'x:Name="VideoToolsFinishPlanText"' `
        -or $xaml -notmatch 'x:Name="VideoToolsStartButton"[\s\S]{0,300}IsEnabled="False"') {
        throw 'The compact Video Tools modal surface or its closed Start gate is missing.'
    }

    $requiredResourceKeys = @(
        'UiVideoToolsTitle',
        'UiVideoToolsRetakeAction',
        'UiVideoToolsRetakeHelp',
        'UiVideoToolsFinishAction',
        'UiVideoToolsFinishHelp',
        'UiVideoToolsRetakeDescription',
        'UiVideoToolsRetakePlanAutomation',
        'UiVideoToolsFinishDescription',
        'UiVideoToolsFinishPlanAutomation',
        'UiVideoToolsStartButton'
    )
    foreach ($resourcePath in @($jaResourcePath, $enResourcePath)) {
        [xml]$resource = Get-Content -Raw -Encoding UTF8 -LiteralPath $resourcePath
        $keys = @($resource.ResourceDictionary.String | ForEach-Object {
            $_.GetAttribute('Key', 'http://schemas.microsoft.com/winfx/2006/xaml')
        })
        foreach ($requiredKey in $requiredResourceKeys) {
            if ($keys -notcontains $requiredKey) {
                throw "Missing localized Video Tools resource $requiredKey in $resourcePath"
            }
        }
    }

    $implementation = Get-Content -Raw -Encoding UTF8 -LiteralPath $implementationPath
    if ($implementation -notmatch 'PV-ENHANCE-VIDEO-TOOLS-001' `
        -or $implementation -notmatch 'aibos-enhancement-video-tools-v1' `
        -or $implementation -notmatch 'sourceVideoJobId' `
        -or $implementation -notmatch 'TryGetDisplayedModalVideoVersion' `
        -or $implementation -notmatch 'TryNormalizeAndValidateVideoH3Prompt' `
        -or $implementation -notmatch 'SendPassiveEnhancementReadAsync\("api/enhance/health"\)' `
        -or $implementation -match 'SendEnhancementEnqueueAsync' `
        -or $implementation -match 'EnsureEnhancementCompanionReadyForExplicitActionAsync' `
        -or $implementation -match '\["sourcePath"\]' `
        -or $implementation -match 'sourceManagedOutputPath') {
        throw 'Video Tools crossed its passive reader-first or Job-ID-only source boundary.'
    }
    $jobsSource = Get-Content -Raw -Encoding UTF8 -LiteralPath $jobsPath
    $smokeSource = Get-Content -Raw -Encoding UTF8 -LiteralPath $smokePath
    if ($implementation -notmatch 'TryReadVideoToolsWorkspaceSnapshot' `
        -or $implementation -notmatch 'TryReadVideoToolsWorkspacePresentationForSmoke' `
        -or $jobsSource -notmatch 'IsVideoToolsReaderOnly' `
        -or $jobsSource -notmatch '!IsVideoToolsReaderOnly[\s\S]{0,200}IsKnownOperation' `
        -or $smokeSource -notmatch 'readerSnapshots' `
        -or $smokeSource -notmatch 'malformedReaderProtected' `
        -or $smokeSource -notmatch 'futureReaderProtected' `
        -or $smokeSource -notmatch 'operationMismatchProtected') {
        throw 'The Video Tools Jobs reader or fail-closed mutation protection is incomplete.'
    }
    $appSource = Get-Content -Raw -Encoding UTF8 -LiteralPath $appPath
    if ($appSource -notmatch '--video-tools-smoke' `
        -or $appSource -notmatch 'CaptureVideoToolsSmoke') {
        throw 'The Video Tools focused smoke dispatch is missing.'
    }

    if ($StaticOnly) {
        function Get-VideoToolsRetakeOraclePlan {
            param(
                [int]$SourceFrameCount,
                [int]$SelectionStartFrame,
                [int]$SelectionEndFrameExclusive
            )
            if ($SourceFrameCount -le 0 `
                -or $SelectionStartFrame -lt 0 `
                -or $SelectionEndFrameExclusive -le $SelectionStartFrame `
                -or $SelectionEndFrameExclusive -gt $SourceFrameCount) {
                return $null
            }
            $selectedFrameCount =
                $SelectionEndFrameExclusive - $SelectionStartFrame
            $actualFrameCount = @(124, 243, 294, 362) |
                Where-Object {
                    $_ -ge $selectedFrameCount -and $_ -le $SourceFrameCount
                } |
                Select-Object -First 1
            if ($null -eq $actualFrameCount) {
                return $null
            }
            $selectedEndFrame = $SelectionEndFrameExclusive - 1
            $centeredStart = [int][Math]::Floor(
                ($SelectionStartFrame + $selectedEndFrame `
                    - $actualFrameCount + 1) / 2.0)
            $actualStartFrame = [Math]::Max(
                0,
                [Math]::Min(
                    $centeredStart,
                    $SourceFrameCount - $actualFrameCount))
            [pscustomobject]@{
                actualStartFrame = $actualStartFrame
                actualFrameCount = $actualFrameCount
                firstAnchorFrame = $actualStartFrame
                lastAnchorFrame =
                    $actualStartFrame + $actualFrameCount - 1
            }
        }

        $shortest = Get-VideoToolsRetakeOraclePlan 124 0 124
        $left = Get-VideoToolsRetakeOraclePlan 362 0 24
        $right = Get-VideoToolsRetakeOraclePlan 362 336 362
        $odd = Get-VideoToolsRetakeOraclePlan 362 120 143
        $rejected = Get-VideoToolsRetakeOraclePlan 240 0 240
        $plannerOracle = $shortest.actualStartFrame -eq 0 `
            -and $shortest.actualFrameCount -eq 124 `
            -and $left.actualStartFrame -eq 0 `
            -and $right.actualStartFrame -eq 238 `
            -and $right.lastAnchorFrame -eq 361 `
            -and $odd.actualStartFrame -eq 69 `
            -and $odd.firstAnchorFrame -eq 69 `
            -and $odd.lastAnchorFrame -eq 192 `
            -and $null -eq $rejected
        if (-not $plannerOracle) {
            throw 'The exact Retake planner boundary oracle failed.'
        }
        [pscustomobject]@{
            ok = $true
            contractId = 'PV-ENHANCE-VIDEO-TOOLS-001'
            contractSha256 = $contractHash
            staticSurface = $true
            plannerOracle = $plannerOracle
            readerFixtureSurface = $true
            runtimeSmoke = 'skipped: net10 SDK and WPF build output are required'
        } | ConvertTo-Json -Depth 4
        return
    }

    $dotNetExecutable = (Get-Command $DotNetPath -ErrorAction Stop).Source
    $dotNetRoot = Split-Path -Parent $dotNetExecutable
    foreach ($entry in $environmentPaths.GetEnumerator()) {
        $previousEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
    $previousEnvironment['DOTNET_ROOT'] = [Environment]::GetEnvironmentVariable('DOTNET_ROOT', 'Process')
    $previousEnvironment['DOTNET_ROOT_X64'] = [Environment]::GetEnvironmentVariable('DOTNET_ROOT_X64', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $dotNetRoot, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT_X64', $dotNetRoot, 'Process')

    if (-not $SkipBuild) {
        & $dotNetExecutable build $project `
            -c $Configuration `
            --artifacts-path $buildRoot `
            --nologo
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    $exe = Get-ChildItem -LiteralPath $buildRoot -Recurse -Filter 'PhotoViewer.Wpf.exe' `
        -ErrorAction Stop | Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($exe) -or -not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw 'The isolated WPF executable was not found.'
    }

    $process = Start-Process -FilePath $exe `
        -ArgumentList @('--video-tools-smoke', ('"{0}"' -f $resultPath)) `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -WindowStyle Hidden `
        -PassThru `
        -Wait
    if ($process.ExitCode -ne 0) {
        $stderr = if (Test-Path -LiteralPath $stderrPath) {
            Get-Content -Raw -LiteralPath $stderrPath
        } else { 'no stderr' }
        $captured = if (Test-Path -LiteralPath $resultPath) {
            Get-Content -Raw -LiteralPath $resultPath
        } else { 'no result' }
        throw "Video Tools smoke exited $($process.ExitCode): $stderr result=$captured"
    }

    $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    if (-not $result.ok `
        -or -not $result.shortestExact `
        -or -not $result.leftClamp `
        -or -not $result.rightClamp `
        -or -not $result.oddPaddingTieBreak `
        -or -not $result.invalidSelectionRejected `
        -or -not $result.capabilityExact `
        -or -not $result.capabilityMalformedRejected `
        -or -not $result.motionDirectorPromptInterop `
        -or -not $result.retakeRequestExact `
        -or -not $result.unsafeRequestRejected `
        -or -not $result.uppercaseUuidAccepted `
        -or -not $result.finishPlan `
        -or -not $result.finishBounds `
        -or -not $result.finishRequestExact `
        -or -not $result.sourceGates `
        -or -not $result.readerSnapshots `
        -or -not $result.retakeReaderProtected `
        -or -not $result.finishReaderProtected `
        -or -not $result.malformedReaderProtected `
        -or -not $result.malformedShapeProtected `
        -or -not $result.futureReaderProtected `
        -or -not $result.operationMismatchProtected `
        -or -not $result.videoProducerDependencyGated `
        -or -not $result.localeFocused `
        -or -not $result.passiveOpen `
        -or $result.healthGets -lt 2 `
        -or $result.mutationRequests -ne 0 `
        -or $result.enqueueCallSites -ne 0) {
        throw ('Video Tools smoke failed: ' + ($result | ConvertTo-Json -Compress -Depth 6))
    }

    [pscustomobject]@{
        ok = $true
        contractId = 'PV-ENHANCE-VIDEO-TOOLS-001'
        protocol = 'aibos-enhancement-video-tools-v1'
        focusedSmoke = $result
    } | ConvertTo-Json -Depth 6
}
finally {
    foreach ($key in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($key, $previousEnvironment[$key], 'Process')
    }
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        if ([string]::Equals([IO.Path]::GetDirectoryName($resolvedRunRoot), $tempRoot, [StringComparison]::OrdinalIgnoreCase) `
            -and [IO.Path]::GetFileName($resolvedRunRoot) -match '^aibos-wpf-video-tools-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
        }
    }
}

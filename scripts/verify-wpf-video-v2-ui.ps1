param(
    [string]$Configuration = 'Release',
    [string]$DotNetPath = 'dotnet',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$contractPath = Join-Path $repoRoot 'contracts\enhancement-video-v2.json'
$xamlPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.xaml'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('aibos-wpf-video-v2-ui-' + [guid]::NewGuid().ToString('N'))))
$runParent = [IO.Path]::GetDirectoryName($runRoot)
$runLeaf = [IO.Path]::GetFileName($runRoot)
if (-not [string]::Equals($runParent, $tempRoot, [StringComparison]::OrdinalIgnoreCase) `
    -or $runLeaf -notmatch '^aibos-wpf-video-v2-ui-[0-9a-f]{32}$') {
    throw "Run root escaped the exact TEMP boundary: $runRoot"
}

$buildRoot = Join-Path $runRoot 'build'
$storesRoot = Join-Path $runRoot 'stores'
$contractFixturePath = Join-Path $runRoot 'enhancement-video-v2.json'
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
    AIBOS_SHARED_ROOT_LOCATOR_PATH = (Join-Path $storesRoot 'shared-root.v1.json')
    AIBOS_VIDEO_V2_CONTRACT_PATH = $contractFixturePath
}

try {
    New-Item -ItemType Directory -Path $runRoot, $storesRoot -Force | Out-Null
    Copy-Item -LiteralPath $contractPath -Destination $contractFixturePath
    $contract = Get-Content -Raw -Encoding UTF8 -LiteralPath $contractPath | ConvertFrom-Json
    if ($contract.contractId -ne 'PV-ENHANCE-VIDEO-002' `
        -or $contract.protocol -ne 'aibos.enhancement-video/v2' `
        -or $contract.passiveHealthGate.field -ne 'capabilities.videoV2' `
        -or $contract.passiveHealthGate.requiredFields -notcontains 'runtimeSealVerified' `
        -or $contract.passiveHealthGate.reasonCodes -notcontains 'MINIMAX_H3_RUNTIME_SEAL_INVALID' `
        -or $contract.profile.canvasPolicy.kind -ne 'source-aspect-aligned-v1' `
        -or $contract.profile.canvasPolicy.maxPixelArea -ne 414720) {
        throw 'The canonical MiniMax H3 video v2 contract identity is invalid.'
    }
    $xaml = Get-Content -Raw -Encoding UTF8 -LiteralPath $xamlPath
    if ($xaml -notmatch 'Tag="minimax-h3"' `
        -or $xaml -notmatch 'x:Name="AppVideoWanControlsPanel"' `
        -or $xaml -notmatch 'x:Name="ModalVideoWanControlsPanel"' `
        -or $xaml -notmatch 'x:Name="ModalVideoWanTuningPanel"' `
        -or $xaml -notmatch 'x:Name="AppVideoPromptTemplateComboBox"' `
        -or $xaml -notmatch 'x:Name="ModalVideoPromptTemplateComboBox"' `
        -or $xaml -notmatch 'x:Name="ModalVideoPromptTextBox"[\s\S]{0,700}Background="\{StaticResource BgPrimary\}"' `
        -or $xaml -notmatch 'x:Name="AppVideoPromptTextBox"[\s\S]{0,700}Foreground="\{StaticResource TextPrimary\}"') {
        throw 'MiniMax H3 UI does not preserve the existing dark resource pattern.'
    }

    $dotNetExecutable = (Get-Command $DotNetPath -ErrorAction Stop).Source
    $dotNetRoot = Split-Path -Parent $dotNetExecutable
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

    foreach ($entry in $environmentPaths.GetEnumerator()) {
        $previousEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
    $previousEnvironment['DOTNET_ROOT'] = [Environment]::GetEnvironmentVariable('DOTNET_ROOT', 'Process')
    $previousEnvironment['DOTNET_ROOT_X64'] = [Environment]::GetEnvironmentVariable('DOTNET_ROOT_X64', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $dotNetRoot, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT_X64', $dotNetRoot, 'Process')

    $process = Start-Process -FilePath $exe `
        -ArgumentList @('--video-v2-ui-smoke', ('"{0}"' -f $resultPath)) `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -WindowStyle Hidden `
        -PassThru `
        -Wait
    if ($process.ExitCode -ne 0) {
        $stderr = if (Test-Path -LiteralPath $stderrPath) {
            Get-Content -Raw -LiteralPath $stderrPath
        } else {
            'no stderr'
        }
        $stdout = if (Test-Path -LiteralPath $stdoutPath) {
            Get-Content -Raw -LiteralPath $stdoutPath
        } else {
            'no stdout'
        }
        $resultText = if (Test-Path -LiteralPath $resultPath) {
            Get-Content -Raw -LiteralPath $resultPath
        } else {
            'no result'
        }
        throw "Video v2 UI smoke exited $($process.ExitCode): stderr=$stderr stdout=$stdout result=$resultText"
    }
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        throw 'Video v2 UI smoke did not produce its result.'
    }

    $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    if (-not $result.ok `
        -or -not $result.h3DefaultOnly `
        -or -not $result.templateExact `
        -or -not $result.h3UnavailableSafe `
        -or -not $result.h3ReservationStatusExact `
        -or -not $result.requestExact `
        -or -not $result.healthExact `
        -or -not $result.invalidSealReasonVisible `
        -or -not $result.duplicateCapabilitiesRejected `
        -or -not $result.duplicateVideoV2Rejected `
        -or -not $result.h3ReadySafe `
        -or -not $result.legacyWanMigratedToH3 `
        -or -not $result.durationExact `
        -or -not $result.canvasPolicyExact `
        -or -not $result.h3ExactUnreadyProfilesFailClosed `
        -or -not $result.h3RetryExactHealth) {
        throw ('Video v2 UI smoke failed: ' + ($result | ConvertTo-Json -Compress))
    }

    [pscustomobject]@{
        ok = $true
        contractId = $contract.contractId
        protocol = $contract.protocol
        healthField = $contract.passiveHealthGate.field
        focusedSmoke = $result
    } | ConvertTo-Json -Depth 5
}
finally {
    foreach ($key in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($key, $previousEnvironment[$key], 'Process')
    }
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        if ([string]::Equals(
                [IO.Path]::GetDirectoryName($resolvedRunRoot),
                $tempRoot,
                [StringComparison]::OrdinalIgnoreCase) `
            -and [IO.Path]::GetFileName($resolvedRunRoot) `
                -match '^aibos-wpf-video-v2-ui-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
        }
    }
}

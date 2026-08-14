[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$DotNetPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$contract = Join-Path $repoRoot 'contracts\enhancement-video-v2.json'
$mediaFixture = Join-Path $repoRoot (
    'local-native\PhotoViewer.Wpf\SmokeFixtures\' +
    'valid-h3-864x480-124f-h264-aac.mp4.b64')
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot (
    'photoviewer-wpf-video-v2-reader-' + [guid]::NewGuid().ToString('N'))))
$buildRoot = Join-Path $runRoot 'build'
$resultPath = Join-Path $runRoot 'result.json'
$process = $null
$previousDotNetRoot = [Environment]::GetEnvironmentVariable('DOTNET_ROOT', 'Process')
$previousDotNetRootX64 = [Environment]::GetEnvironmentVariable('DOTNET_ROOT_X64', 'Process')

if (-not $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Verifier root must stay under TEMP.'
}
if (-not (Test-Path -LiteralPath $contract -PathType Leaf)) {
    throw "MiniMax H3 video v2 contract was not found: $contract"
}
if (-not (Test-Path -LiteralPath $mediaFixture -PathType Leaf)) {
    throw "Synthetic H3 media fixture was not found: $mediaFixture"
}

try {
    $readerContract = Get-Content -LiteralPath $contract -Raw -Encoding UTF8 |
        ConvertFrom-Json
    $asciiVector = @($readerContract.readerFixture.stableSnapshotHashVectors |
        Where-Object { $_.id -eq 'ascii-prompt' })
    $japaneseVector = @($readerContract.readerFixture.stableSnapshotHashVectors |
        Where-Object { $_.id -eq 'utf8-japanese-prompt' })
    $portraitVector = @($readerContract.readerFixture.stableSnapshotHashVectors |
        Where-Object { $_.id -eq 'portrait-source-aspect' })
    if ($asciiVector.Count -ne 1 -or
        $japaneseVector.Count -ne 1 -or
        $portraitVector.Count -ne 1) {
        throw 'Canonical stable snapshot hash vectors are missing or ambiguous.'
    }
    $asciiExpectedPresetHash = [string]$asciiVector[0].expectedPresetHash
    $japaneseExpectedPresetHash = [string]$japaneseVector[0].expectedPresetHash
    $portraitExpectedPresetHash = [string]$portraitVector[0].expectedPresetHash
    $fixtureText = Get-Content -LiteralPath $mediaFixture -Raw -Encoding ASCII
    $fixtureBytes = [Convert]::FromBase64String(
        [Text.RegularExpressions.Regex]::Replace($fixtureText, '\s', ''))
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $fixtureSha256 = [BitConverter]::ToString(
            $sha256.ComputeHash($fixtureBytes)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
    if ($fixtureBytes.Length -ne 8634 -or
        $fixtureSha256 -ne '96c2e817c0dc7e0ee2b2d4865351d7d86894c7cb4ff6d084e8c440e3bd928002') {
        throw 'Synthetic H3 media fixture bytes or SHA-256 are not exact.'
    }

    New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    $buildOutput = $buildRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $dotNetExecutable = (Get-Command $DotNetPath -ErrorAction Stop).Source
    $dotNetRoot = Split-Path -Parent $dotNetExecutable
    & $dotNetExecutable build $project `
        -c $Configuration `
        "-p:OutputPath=$buildOutput" `
        --nologo `
        -v:minimal
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $exe = Join-Path $buildRoot 'PhotoViewer.Wpf.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "WPF executable was not found: $exe"
    }

    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $dotNetRoot, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT_X64', $dotNetRoot, 'Process')

    $process = Start-Process -FilePath $exe `
        -ArgumentList @(
            '--video-v2-reader-smoke',
            ('"{0}"' -f $resultPath),
            '--contract',
            ('"{0}"' -f $contract),
            '--media-fixture-base64',
            ('"{0}"' -f $mediaFixture)
        ) `
        -WindowStyle Hidden `
        -PassThru `
        -Wait
    if ($process.ExitCode -ne 0) {
        $details = if (Test-Path -LiteralPath $resultPath) {
            Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8
        }
        else {
            'no result file'
        }
        throw "video v2 reader smoke exited $($process.ExitCode): $details"
    }

    $smoke = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 |
        ConvertFrom-Json
    $required = @(
        'contractIdentity',
        'sourceContract',
        'sourceDimensionsExact',
        'stableHashExact',
        'japaneseHashInterop',
        'portraitHashInterop',
        'canvasPolicyVectorsExact',
        'exifOrientationMetadataExact',
        'exifWriterCanvasExact',
        'exifWorkspaceReaderExact',
        'exifPlaybackReaderExact',
        'exifUnswappedCanvasProtected',
        'exifFixturesReadOnly',
        'exifFixturesIsolated',
        'mediaFixtureExact',
        'ownedOutputName',
        'snapshotHashesExact',
        'operationsExact',
        'labelsExact',
        'validWorkspace',
        'validPlayback',
        'selectedStepsPlayback',
        'invalidPlaybackProtected',
        'sourceAspectPolicyProtected',
        'exactSets',
        'unknownFieldsPresent',
        'modalSourceSelected',
        'modalOpened',
        'modalMediaOpened',
        'modalPlaybackProgress',
        'modalPauseResume',
        'modalMediaFailureFallback',
        'validMediaPreserved',
        'readOnly',
        'isolated'
    )
    $failed = @($required | Where-Object { $smoke.$_ -ne $true })
    if ($smoke.ok -ne $true -or
        $failed.Count -gt 0 -or
        $smoke.mutationRequests -ne 0 -or
        $smoke.playbackFps -ne 24 -or
        $smoke.frameCount -ne 124 -or
        $smoke.videoWidth -ne 864 -or
        $smoke.videoHeight -ne 480 -or
        @($smoke.exifOrientationsCovered).Count -ne 4 -or
        (($smoke.exifOrientationsCovered -join ',') -ne '5,6,7,8') -or
        $smoke.exifStoredWidth -ne 96 -or
        $smoke.exifStoredHeight -ne 64 -or
        $smoke.exifWriterWidth -ne 512 -or
        $smoke.exifWriterHeight -ne 768 -or
        $smoke.exifUnswappedWidth -ne 768 -or
        $smoke.exifUnswappedHeight -ne 512 -or
        [Math]::Abs([double]$smoke.durationSeconds - (124.0 / 24.0)) -gt 1e-12 -or
        $smoke.audio -ne $true -or
        $smoke.mediaFixtureSha256 -ne $fixtureSha256 -or
        $null -ne $smoke.modalPlaybackException -or
        $smoke.validHash.Substring(0, 12) -ne $asciiExpectedPresetHash -or
        $smoke.japaneseHash.Substring(0, 12) -ne $japaneseExpectedPresetHash -or
        $smoke.portraitHash.Substring(0, 12) -ne $portraitExpectedPresetHash -or
        $smoke.asciiExpectedPresetHash -ne $asciiExpectedPresetHash -or
        $smoke.japaneseExpectedPresetHash -ne $japaneseExpectedPresetHash -or
        $smoke.portraitExpectedPresetHash -ne $portraitExpectedPresetHash) {
        throw "video v2 reader contract failed ($($failed -join ', ')): $(
            Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8)"
    }

    $smoke | ConvertTo-Json -Depth 8
}
finally {
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $previousDotNetRoot, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT_X64', $previousDotNetRootX64, 'Process')
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        if (-not $resolvedRunRoot.StartsWith(
                $tempPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a verifier path outside TEMP: $resolvedRunRoot"
        }
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}

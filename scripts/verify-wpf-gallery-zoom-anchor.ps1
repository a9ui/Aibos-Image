param(
    [string]$Configuration = "Release",
    [string]$OutputPath = (Join-Path $env:TEMP ("photoviewer-wpf-gallery-zoom-anchor-" + [guid]::NewGuid().ToString('N') + ".json"))
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net8.0-windows\PhotoViewer.Wpf.exe"
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ("photoviewer-wpf-gallery-zoom-" + [guid]::NewGuid().ToString('N'))))
$fixtureParent = [IO.Path]::GetDirectoryName($fixtureRoot)
$fixtureLeaf = [IO.Path]::GetFileName($fixtureRoot)
if (-not [string]::Equals($fixtureParent, $tempRoot, [StringComparison]::OrdinalIgnoreCase) -or $fixtureLeaf -notmatch '^photoviewer-wpf-gallery-zoom-[0-9a-f]{32}$') {
    throw "Fixture root escaped the exact TEMP boundary: $fixtureRoot"
}
if ($OutputPath.Contains('"')) { throw 'OutputPath cannot contain a double quote.' }
$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$tempRootPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
if (-not $outputFullPath.StartsWith($tempRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputPath must stay under TEMP: $outputFullPath"
}
$statePath = Join-Path ([IO.Path]::GetDirectoryName($outputFullPath)) (([IO.Path]::GetFileNameWithoutExtension($outputFullPath)) + '-state.json')
$stdoutPath = $outputFullPath + '.stdout.log'
$stderrPath = $outputFullPath + '.stderr.log'

dotnet build $project -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "WPF executable was not found: $exe" }

$process = $null
try {
    New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
    $seedPath = Join-Path $fixtureRoot 'seed.png'
    $pngBytes = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAQAAAADCAIAAAA7l3agAAAAFElEQVR4nGPkEpFjQAJMDKiAVD4AANMABQ+5f2QAAAAASUVORK5CYII=')
    [IO.File]::WriteAllBytes($seedPath, $pngBytes)
    $timestampBase = [DateTime]::UtcNow.AddDays(-2)
    [IO.File]::SetLastWriteTimeUtc($seedPath, $timestampBase.AddMinutes(-1))
    foreach ($index in 0..239) {
        $zoomPath = Join-Path $fixtureRoot ("zoom-{0:D4}.png" -f $index)
        Copy-Item -LiteralPath $seedPath -Destination $zoomPath
        [IO.File]::SetLastWriteTimeUtc($zoomPath, $timestampBase.AddMinutes($index))
    }
    $duplicateA = Join-Path $fixtureRoot 'duplicate-a'
    $duplicateB = Join-Path $fixtureRoot 'duplicate-b'
    [IO.Directory]::CreateDirectory($duplicateA) | Out-Null
    [IO.Directory]::CreateDirectory($duplicateB) | Out-Null
    Copy-Item -LiteralPath $seedPath -Destination (Join-Path $duplicateA 'duplicate.png')
    Copy-Item -LiteralPath $seedPath -Destination (Join-Path $duplicateB 'duplicate.png')
    [IO.File]::SetLastWriteTimeUtc((Join-Path $duplicateA 'duplicate.png'), $timestampBase.AddMinutes(119.25))
    [IO.File]::SetLastWriteTimeUtc((Join-Path $duplicateB 'duplicate.png'), $timestampBase.AddMinutes(119.75))

    $legacyState = @{
        Version = 2
        CardWidth = 5
        LegacyZoomMigrationSentinel = @{ keep = 'yes' }
    } | ConvertTo-Json -Depth 4
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($statePath)) | Out-Null
    [IO.File]::WriteAllText($statePath, $legacyState, [Text.UTF8Encoding]::new($false))
    Remove-Item -LiteralPath $outputFullPath -Force -ErrorAction SilentlyContinue

    $process = Start-Process -FilePath $exe `
        -ArgumentList @('--grid-zoom-smoke', ('"{0}"' -f $outputFullPath), '--folder', ('"{0}"' -f $fixtureRoot)) `
        -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath `
        -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) {
        $stderr = if (Test-Path -LiteralPath $stderrPath) { Get-Content -Raw -LiteralPath $stderrPath } else { 'no stderr' }
        throw "Grid zoom smoke process exited $($process.ExitCode): $stderr"
    }
    if (-not (Test-Path -LiteralPath $outputFullPath -PathType Leaf)) { throw "Grid zoom smoke did not produce $outputFullPath" }

    $result = Get-Content -Raw -LiteralPath $outputFullPath | ConvertFrom-Json
    $failures = @()
    if ($result.ok -ne $true) { $failures += $result.message }
    if ($result.minimumWidth -ne 20 -or $result.maximumWidth -ne 600) { $failures += "zoom endpoints were $($result.minimumWidth)/$($result.maximumWidth)" }
    if ($result.maximumColumns -ne 1 -or $result.maximumForcedSingleColumn -ne $true) { $failures += 'maximum endpoint was not exactly one column' }
    if ($result.maximumRealized -lt 1 -or $result.maximumRealized -gt $result.maximumRealizedBound) { $failures += 'Grid realization was not bounded at maximum zoom' }
    foreach ($name in @('gallerySurfaceWheelIn','gallerySurfaceWheelOut','galleryScrollbarWheelIn','galleryScrollbarWheelOut','nonGalleryWheelIsolation','middleAutoScroll','selectedAnchorUsesSelection','duplicateBasenameCanonicalAnchor','sidebarCollapseAnchorKept','sidebarExpandAnchorKept','rightPanelResizeAnchorKept','resizeAnchorKept','dpiAnchorKept','noSelectionCollapseKept','noSelectionExpandKept','listUsesRecyclingVirtualization','listBounded','listWheelNative','listZoomRejected')) {
        if ($result.$name -ne $true) { $failures += "$name was false" }
    }
    foreach ($name in @('sidebarCollapseDrift','sidebarExpandDrift','rightPanelResizeDrift','resizeDrift','dpiDrift','noSelectionCollapseDrift','noSelectionExpandDrift')) {
        if ($null -eq $result.$name -or [double]$result.$name -gt 8) { $failures += "$name was $($result.$name) px" }
    }
    if ($result.migratedStateCardWidth -ne 20) { $failures += "legacy persisted width was not clamped to 20 at migration ($($result.migratedStateCardWidth))" }
    if ($result.migrationUnknownStatePreserved -ne $true) { $failures += 'unknown state field was not preserved during zoom migration' }

    $legacy40OutputPath = Join-Path ([IO.Path]::GetDirectoryName($outputFullPath)) (([IO.Path]::GetFileNameWithoutExtension($outputFullPath)) + '-legacy40.json')
    $legacy40StatePath = Join-Path ([IO.Path]::GetDirectoryName($legacy40OutputPath)) (([IO.Path]::GetFileNameWithoutExtension($legacy40OutputPath)) + '-state.json')
    $legacy40State = @{
        Version = 2
        CardWidth = 40
        LegacyZoomMigrationSentinel = @{ keep = 'forty' }
    } | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText($legacy40StatePath, $legacy40State, [Text.UTF8Encoding]::new($false))
    Remove-Item -LiteralPath $legacy40OutputPath -Force -ErrorAction SilentlyContinue
    $legacy40Process = Start-Process -FilePath $exe `
        -ArgumentList @('--grid-zoom-smoke', ('"{0}"' -f $legacy40OutputPath), '--folder', ('"{0}"' -f $fixtureRoot), '--expected-initial-width', '40') `
        -RedirectStandardOutput ($legacy40OutputPath + '.stdout.log') -RedirectStandardError ($legacy40OutputPath + '.stderr.log') `
        -WindowStyle Hidden -PassThru -Wait
    if ($legacy40Process.ExitCode -ne 0) { $failures += "legacy 40px smoke process exited $($legacy40Process.ExitCode)" }
    if (-not (Test-Path -LiteralPath $legacy40OutputPath -PathType Leaf)) {
        $failures += 'legacy 40px smoke did not produce a result'
    }
    else {
        $legacy40Result = Get-Content -Raw -LiteralPath $legacy40OutputPath | ConvertFrom-Json
        if ($legacy40Result.ok -ne $true -or $legacy40Result.initialWidth -ne 40) { $failures += "legacy 40px width was not preserved ($($legacy40Result.initialWidth))" }
        if ($legacy40Result.migratedStateCardWidth -ne 40 -or $legacy40Result.migrationUnknownStatePreserved -ne $true) { $failures += 'legacy 40px state or unknown field was not preserved at migration' }
        $result | Add-Member -Force -NotePropertyName legacy40InitialWidth -NotePropertyValue $legacy40Result.initialWidth
        $result | Add-Member -Force -NotePropertyName legacy40StatePreserved -NotePropertyValue ($legacy40Result.migratedStateCardWidth -eq 40 -and $legacy40Result.migrationUnknownStatePreserved -eq $true)
    }

    $result | Add-Member -Force -NotePropertyName persistedCardWidth -NotePropertyValue $result.migratedStateCardWidth
    $result | Add-Member -Force -NotePropertyName unknownStatePreserved -NotePropertyValue $result.migrationUnknownStatePreserved
    $result | ConvertTo-Json -Depth 8
    if ($failures.Count -gt 0) { throw ('WPF gallery zoom/anchor gate failed: ' + ($failures -join '; ')) }
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
        $resolvedParent = [IO.Path]::GetDirectoryName($resolvedFixture)
        $resolvedLeaf = [IO.Path]::GetFileName($resolvedFixture)
        if ([string]::Equals($resolvedParent, $tempRoot, [StringComparison]::OrdinalIgnoreCase) -and $resolvedLeaf -match '^photoviewer-wpf-gallery-zoom-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
        }
        else {
            throw "Refusing to clean unexpected fixture path: $resolvedFixture"
        }
    }
}

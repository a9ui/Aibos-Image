param(
    [Parameter(Mandatory = $true)]
    [string]$LegacyRepo,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$BrowserCommit,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$legacyRoot = [IO.Path]::GetFullPath($LegacyRepo)
$legacyGit = Join-Path $legacyRoot '.git'
$vitest = Join-Path $legacyRoot 'node_modules\.bin\vitest.cmd'
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempBase + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempBase ('aibos-cross-root-matrix-' + [guid]::NewGuid().ToString('N'))))
$completed = $false

if (-not $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a cross-repository verifier root outside TEMP: $runRoot"
}
if (-not (Test-Path -LiteralPath $legacyGit) -or -not (Test-Path -LiteralPath $vitest -PathType Leaf)) {
    throw 'The legacy repository or its existing Vitest runtime is unavailable.'
}

$browserTest = @'
import fs from 'fs';
import path from 'path';
import { resolveSharedCachePath } from './sharedProjectRoot';

test('resolves every durable path through the process locator route', () => {
  const paths = {
    favorites: resolveSharedCachePath('favorites.json'),
    seen: resolveSharedCachePath('seen.json'),
    settings: resolveSharedCachePath('settings.json'),
    albums: resolveSharedCachePath('albums.json'),
    searchHistory: resolveSharedCachePath('search-history.json'),
    recentFolders: resolveSharedCachePath('recent-folders.json'),
    enhancementJobs: path.join(resolveSharedCachePath('enhance'), 'jobs.json'),
    enhancementOutputs: path.join(resolveSharedCachePath('enhance'), 'outputs'),
  };
  fs.writeFileSync(process.env.AIBOS_MATRIX_RESULT!, JSON.stringify(paths), 'utf8');
  expect(Object.values(paths).every((value) => path.isAbsolute(value))).toBe(true);
});
'@

try {
    New-Item -ItemType Directory -Path $runRoot | Out-Null
    $legacyBefore = git -C $legacyRoot status --porcelain=v1 -uall | Out-String
    $resolvedCommit = (git -C $legacyRoot rev-parse "$BrowserCommit^{commit}").Trim()
    if ($LASTEXITCODE -ne 0 -or $resolvedCommit -cne $BrowserCommit) {
        throw "The requested Browser commit is unavailable: $BrowserCommit"
    }

    $archiveZip = Join-Path $runRoot 'h25.zip'
    $archiveRoot = Join-Path $runRoot 'h25'
    git -C $legacyRoot archive --format=zip -o $archiveZip $BrowserCommit
    if ($LASTEXITCODE -ne 0) { throw 'Could not export the exact Browser commit.' }
    Expand-Archive -LiteralPath $archiveZip -DestinationPath $archiveRoot

    $fixtureRoot = Join-Path $runRoot 'fixture'
    $wpfBuild = Join-Path $runRoot 'wpf-build'
    $wpfResultPath = Join-Path $runRoot 'wpf-result.json'
    & dotnet build $project -c $Configuration "-p:OutputPath=$wpfBuild" --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'WPF matrix build failed.' }
    & dotnet (Join-Path $wpfBuild 'PhotoViewer.Wpf.dll') `
        --shared-root-activation-smoke $wpfResultPath `
        --case valid `
        --temp-root $fixtureRoot
    if ($LASTEXITCODE -ne 0) { throw 'WPF shared-root activation matrix failed.' }
    $wpf = Get-Content -Raw -LiteralPath $wpfResultPath | ConvertFrom-Json
    if ($wpf.ok -ne $true -or $wpf.treeUnchanged -ne $true) {
        throw 'WPF activation changed the synthetic durable-state tree.'
    }

    $browserResultPath = Join-Path $runRoot 'browser-result.json'
    $browserTestPath = Join-Path $archiveRoot 'src\lib\crossRootMatrix.generated.test.ts'
    $browserConfigPath = Join-Path $archiveRoot 'vitest.cross-root.generated.config.ts'
    $koffiEntry = (Resolve-Path -LiteralPath (Join-Path $legacyRoot 'node_modules\koffi\index.js')).Path.Replace('\', '/')
    $escapedKoffiEntry = $koffiEntry.Replace("'", "\'")
    [IO.File]::WriteAllText($browserTestPath, $browserTest, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        $browserConfigPath,
        "export default { resolve: { alias: { koffi: '$escapedKoffiEntry' } }, test: { globals: true, environment: 'node' } };`n",
        [Text.UTF8Encoding]::new($false))
    foreach ($name in @(
        'PVU_FAVORITES_PATH',
        'PVU_SEEN_PATH',
        'PVU_SETTINGS_PATH',
        'PVU_ALBUMS_PATH',
        'PVU_SEARCH_HISTORY_PATH',
        'PVU_RECENT_FOLDERS_PATH',
        'PVU_ENHANCE_ROOT'
    )) {
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }
    $env:AIBOS_SHARED_ROOT_LOCATOR_PATH = Join-Path $fixtureRoot 'shared-root.v1.json'
    $env:AIBOS_MATRIX_RESULT = $browserResultPath
    $dataRoot = Join-Path $fixtureRoot 'data'
    $dataBefore = @(Get-ChildItem -LiteralPath $dataRoot -Force -Recurse)

    & $vitest run --root $archiveRoot --config $browserConfigPath $browserTestPath
    if ($LASTEXITCODE -ne 0) { throw 'The exact Browser path-matrix test failed.' }
    $browser = Get-Content -Raw -LiteralPath $browserResultPath | ConvertFrom-Json
    $dataAfter = @(Get-ChildItem -LiteralPath $dataRoot -Force -Recurse)

    $wpfPaths = [ordered]@{
        favorites = $wpf.storePaths.PHOTOVIEWER_WPF_FAVORITES_PATH
        seen = $wpf.storePaths.PHOTOVIEWER_WPF_SEEN_PATH
        settings = $wpf.storePaths.PHOTOVIEWER_WPF_SETTINGS_PATH
        albums = $wpf.storePaths.PHOTOVIEWER_WPF_ALBUMS_PATH
        searchHistory = $wpf.storePaths.PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH
        recentFolders = $wpf.storePaths.PHOTOVIEWER_WPF_RECENT_PATH
        enhancementJobs = $wpf.storePaths.PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH
        enhancementOutputs = $wpf.managedOutputsRoot
    }
    $mismatches = @()
    foreach ($key in $wpfPaths.Keys) {
        if (-not [string]::Equals(
            [IO.Path]::GetFullPath($wpfPaths[$key]),
            [IO.Path]::GetFullPath($browser.$key),
            [StringComparison]::OrdinalIgnoreCase)) {
            $mismatches += $key
        }
    }

    $legacyAfter = git -C $legacyRoot status --porcelain=v1 -uall | Out-String
    $result = [ordered]@{
        ok = $mismatches.Count -eq 0 `
            -and $dataBefore.Count -eq 0 `
            -and $dataAfter.Count -eq 0 `
            -and $legacyBefore -ceq $legacyAfter
        browserCommit = $BrowserCommit
        browserTree = (git -C $legacyRoot show -s --format=%T $BrowserCommit).Trim()
        pathCount = $wpfPaths.Count
        mismatches = $mismatches
        perStoreOverridesUnset = $true
        sharedDataEntriesBefore = $dataBefore.Count
        sharedDataEntriesAfter = $dataAfter.Count
        wpfActivationTreeUnchanged = $wpf.treeUnchanged
        legacyWorktreeUnchanged = $legacyBefore -ceq $legacyAfter
    }
    $result | ConvertTo-Json -Depth 5
    if (-not $result.ok) { throw 'Cross-repository durable path matrix failed.' }
    $completed = $true
}
finally {
    $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
    if (-not $resolvedRunRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a verifier root outside TEMP: $resolvedRunRoot"
    }
    if (Test-Path -LiteralPath $resolvedRunRoot) {
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
    if (-not $completed -and (Test-Path -LiteralPath $resolvedRunRoot)) {
        Write-Warning "Cross-repository verifier artifacts remain at $resolvedRunRoot"
    }
}

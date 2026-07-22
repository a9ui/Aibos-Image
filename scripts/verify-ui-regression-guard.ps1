param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

$browserFiles = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src\components') -Filter '*.tsx' -File |
    Where-Object { $_.Name -notlike '*.test.tsx' }
$targets = @($browserFiles.FullName) + @(
    (Join-Path $repoRoot 'src\app\page.tsx'),
    (Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.xaml')
)
$runtimeTargets = @(
    (Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.xaml.cs')
)
$browserStyles = Join-Path $repoRoot 'src\app\globals.css'
$browserDeleteRoute = Join-Path $repoRoot 'src\app\api\delete\route.ts'
$browserDeleteHandler = Join-Path $repoRoot 'src\app\api\delete\deleteHandler.ts'
$wpfRuntime = $runtimeTargets[0]

$forbidden = [ordered]@{
    'retired Quick Search control' = '(?i)quick\s*search'
    'retired relative-date preset control' = '(?i)(Content\s*=\s*"\s*(Today|7d|30d|This year)\s*"|>\s*(Today|7d|30d|This year)\s*<)'
    'retired Favorite threshold label' = '(?i)(Lv|Favorite|★|\*)\s*[1-5]\s*\+'
}

$findings = @()
foreach ($target in $targets) {
    if (-not (Test-Path -LiteralPath $target)) {
        throw "Required UI source is missing: $target"
    }
    foreach ($rule in $forbidden.GetEnumerator()) {
        $matches = Select-String -LiteralPath $target -Pattern $rule.Value -AllMatches
        foreach ($match in $matches) {
            $findings += [pscustomobject]@{
                Rule = $rule.Key
                File = [IO.Path]::GetRelativePath($repoRoot, $target)
                Line = $match.LineNumber
                Text = $match.Line.Trim()
            }
        }
    }
}

$runtimeForbidden = [ordered]@{
    'retired relative-date runtime preset API' = '(?i)(DatePresetTodayValue|DatePreset7DaysValue|DatePreset30DaysValue|DatePresetThisYearValue|DateRangeForPreset|SetDatePresetForSmoke)'
    'runtime state must not write an unchecked preset token' = 'DatePreset\s*=\s*_datePreset'
}
foreach ($target in $runtimeTargets) {
    if (-not (Test-Path -LiteralPath $target)) {
        throw "Required runtime source is missing: $target"
    }
    foreach ($rule in $runtimeForbidden.GetEnumerator()) {
        $matches = Select-String -LiteralPath $target -Pattern $rule.Value -AllMatches
        foreach ($match in $matches) {
            $findings += [pscustomobject]@{
                Rule = $rule.Key
                File = [IO.Path]::GetRelativePath($repoRoot, $target)
                Line = $match.LineNumber
                Text = $match.Line.Trim()
            }
        }
    }
}

if (-not (Test-Path -LiteralPath $browserStyles)) {
    throw "Required Browser stylesheet is missing: $browserStyles"
}
$browserStyleText = Get-Content -LiteralPath $browserStyles -Raw
if ($browserStyleText -notmatch '(?m)^\s*--sidebar-width:\s*240px\s*;') {
    $findings += [pscustomobject]@{
        Rule = 'Browser desktop sidebar width token must stay fixed at 240px'
        File = [IO.Path]::GetRelativePath($repoRoot, $browserStyles)
        Line = 0
        Text = '--sidebar-width: 240px; is missing'
    }
}

$sidebarBlock = [regex]::Match($browserStyleText, '(?ms)^\s*\.sidebar\s*\{(?<Body>.*?)^\s*\}')
if (-not $sidebarBlock.Success) {
    $findings += [pscustomobject]@{
        Rule = 'Browser desktop sidebar fixed-width block is required'
        File = [IO.Path]::GetRelativePath($repoRoot, $browserStyles)
        Line = 0
        Text = '.sidebar block is missing'
    }
} else {
    foreach ($property in @('width', 'min-width', 'max-width', 'flex-basis')) {
        if ($sidebarBlock.Groups['Body'].Value -notmatch "(?m)^\s*$([regex]::Escape($property)):\s*var\(--sidebar-width\)\s*;") {
            $findings += [pscustomobject]@{
                Rule = "Browser desktop sidebar $property must use the fixed 240px token"
                File = [IO.Path]::GetRelativePath($repoRoot, $browserStyles)
                Line = 0
                Text = "$property`: var(--sidebar-width); is missing from .sidebar"
            }
        }
    }
}

# Source-image Delete is a product safety boundary. Derived thumbnails, atomic
# write residue, and explicitly managed Enhancement outputs have separate
# ownership and are intentionally outside this source-delete contract.
foreach ($target in @($browserDeleteRoute, $browserDeleteHandler, $wpfRuntime)) {
    if (-not (Test-Path -LiteralPath $target)) {
        throw "Required source-delete implementation is missing: $target"
    }
}

$browserDeleteRouteText = Get-Content -LiteralPath $browserDeleteRoute -Raw
$browserDeleteHandlerText = Get-Content -LiteralPath $browserDeleteHandler -Raw
$wpfRuntimeText = Get-Content -LiteralPath $wpfRuntime -Raw

$browserRecycleStart = $browserDeleteRouteText.IndexOf('async function moveFileToRecycleBin', [StringComparison]::Ordinal)
$browserRecycleEnd = $browserDeleteRouteText.IndexOf('async function removeDerivedImages', [StringComparison]::Ordinal)
if ($browserRecycleStart -lt 0 -or $browserRecycleEnd -le $browserRecycleStart) {
    $findings += [pscustomobject]@{
        Rule = 'Browser source Delete backend must remain a distinct Recycle Bin operation'
        File = [IO.Path]::GetRelativePath($repoRoot, $browserDeleteRoute)
        Line = 0
        Text = 'moveFileToRecycleBin production backend could not be isolated'
    }
} else {
    $browserRecycleBackend = $browserDeleteRouteText.Substring(
        $browserRecycleStart,
        $browserRecycleEnd - $browserRecycleStart)
    if ($browserRecycleBackend -notmatch [regex]::Escape('[Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile') `
        -or $browserRecycleBackend -notmatch "'SendToRecycleBin'") {
        $findings += [pscustomobject]@{
            Rule = 'Browser source Delete backend must use Windows SendToRecycleBin'
            File = [IO.Path]::GetRelativePath($repoRoot, $browserDeleteRoute)
            Line = 0
            Text = 'Microsoft.VisualBasic DeleteFile(...SendToRecycleBin) is missing'
        }
    }
    if ($browserRecycleBackend -match '(?i)(DeletePermanently|fs\s*\.\s*(?:promises\s*\.\s*)?(?:rm|unlink)\s*\(|\b(?:rm|unlink)Sync\s*\()') {
        $findings += [pscustomobject]@{
            Rule = 'Browser source Delete backend must not contain a permanent-delete fallback'
            File = [IO.Path]::GetRelativePath($repoRoot, $browserDeleteRoute)
            Line = 0
            Text = 'hard-delete API found inside moveFileToRecycleBin'
        }
    }
}

if ($browserDeleteRouteText -notmatch '(?m)^\s*recycleFile:\s*moveFileToRecycleBin,\s*$') {
    $findings += [pscustomobject]@{
        Rule = 'Browser DELETE route must wire the production Recycle Bin backend'
        File = [IO.Path]::GetRelativePath($repoRoot, $browserDeleteRoute)
        Line = 0
        Text = 'createDeleteHandler is not wired to moveFileToRecycleBin'
    }
}

$browserRecycleCalls = [regex]::Matches(
    $browserDeleteHandlerText,
    'await\s+dependencies\s*\.\s*recycleFile\s*\(\s*targetPath\s*\)')
if ($browserRecycleCalls.Count -ne 1) {
    $findings += [pscustomobject]@{
        Rule = 'Browser source handler must cross exactly one injected Recycle Bin boundary'
        File = [IO.Path]::GetRelativePath($repoRoot, $browserDeleteHandler)
        Line = 0
        Text = "expected one awaited dependencies.recycleFile(targetPath) call; found $($browserRecycleCalls.Count)"
    }
}
if ($browserDeleteHandlerText -match '(?i)(DeletePermanently|fs\s*\.\s*(?:promises\s*\.\s*)?(?:rm|unlink)\s*\(|\b(?:rm|unlink)Sync\s*\(|FileSystem\s*\.\s*DeleteFile\s*\()') {
    $findings += [pscustomobject]@{
        Rule = 'Browser source handler must not own a direct or fallback file deletion API'
        File = [IO.Path]::GetRelativePath($repoRoot, $browserDeleteHandler)
        Line = 0
        Text = 'direct file-delete API found; source deletion must stay behind recycleFile'
    }
}

$wpfRecycleStart = $wpfRuntimeText.IndexOf('private static RecycleBinDeleteResult SendFileToWindowsRecycleBin', [StringComparison]::Ordinal)
$wpfRecycleEnd = $wpfRuntimeText.IndexOf('private void SetStatusToast', [StringComparison]::Ordinal)
if ($wpfRecycleStart -lt 0 -or $wpfRecycleEnd -le $wpfRecycleStart) {
    $findings += [pscustomobject]@{
        Rule = 'WPF source Delete backend must remain a distinct Recycle Bin operation'
        File = [IO.Path]::GetRelativePath($repoRoot, $wpfRuntime)
        Line = 0
        Text = 'SendFileToWindowsRecycleBin production backend could not be isolated'
    }
} else {
    $wpfRecycleBackend = $wpfRuntimeText.Substring($wpfRecycleStart, $wpfRecycleEnd - $wpfRecycleStart)
    if ($wpfRecycleBackend -notmatch 'Microsoft\.VisualBasic\.FileIO\.FileSystem\.DeleteFile\s*\(' `
        -or $wpfRecycleBackend -notmatch 'Microsoft\.VisualBasic\.FileIO\.RecycleOption\.SendToRecycleBin' `
        -or $wpfRecycleBackend -notmatch 'Microsoft\.VisualBasic\.FileIO\.UICancelOption\.ThrowException') {
        $findings += [pscustomobject]@{
            Rule = 'WPF source Delete backend must explicitly use RecycleOption.SendToRecycleBin and UICancelOption.ThrowException'
            File = [IO.Path]::GetRelativePath($repoRoot, $wpfRuntime)
            Line = 0
            Text = 'Microsoft.VisualBasic DeleteFile(...RecycleOption.SendToRecycleBin, UICancelOption.ThrowException) is missing'
        }
    }
    if ($wpfRecycleBackend -match '(?i)(DeletePermanently|\bFile\s*\.\s*Delete\s*\()') {
        $findings += [pscustomobject]@{
            Rule = 'WPF source Delete backend must not contain a permanent-delete fallback'
            File = [IO.Path]::GetRelativePath($repoRoot, $wpfRuntime)
            Line = 0
            Text = 'hard-delete API found inside SendFileToWindowsRecycleBin'
        }
    }
}

if ($wpfRuntimeText -notmatch '_recycleBinDelete\s*=\s*SendFileToWindowsRecycleBin\s*;') {
    $findings += [pscustomobject]@{
        Rule = 'WPF source Delete workflow must default to the production Recycle Bin backend'
        File = [IO.Path]::GetRelativePath($repoRoot, $wpfRuntime)
        Line = 0
        Text = '_recycleBinDelete is not initialized with SendFileToWindowsRecycleBin'
    }
}

$wpfDeleteWorkflowStart = $wpfRuntimeText.IndexOf('private bool ExecuteDelete', [StringComparison]::Ordinal)
$wpfDeleteWorkflowEnd = $wpfRuntimeText.IndexOf('private bool TryValidateDelete', [StringComparison]::Ordinal)
if ($wpfDeleteWorkflowStart -lt 0 -or $wpfDeleteWorkflowEnd -le $wpfDeleteWorkflowStart) {
    $findings += [pscustomobject]@{
        Rule = 'WPF single/bulk source Delete workflow must remain inspectable'
        File = [IO.Path]::GetRelativePath($repoRoot, $wpfRuntime)
        Line = 0
        Text = 'ExecuteDelete workflow could not be isolated'
    }
} else {
    $wpfDeleteWorkflow = $wpfRuntimeText.Substring(
        $wpfDeleteWorkflowStart,
        $wpfDeleteWorkflowEnd - $wpfDeleteWorkflowStart)
    if ($wpfDeleteWorkflow -match '(?i)(DeletePermanently|\bFile\s*\.\s*Delete\s*\()') {
        $findings += [pscustomobject]@{
            Rule = 'WPF single/bulk source Delete workflow must not hard-delete files'
            File = [IO.Path]::GetRelativePath($repoRoot, $wpfRuntime)
            Line = 0
            Text = 'hard-delete API found in the source Delete workflow'
        }
    }
}

if ($findings.Count -gt 0) {
    $findings | Format-Table -AutoSize | Out-String | Write-Host
    throw 'PhotoViewer UI or source-delete safety semantics regressed.'
}

[pscustomobject]@{
    ok = $true
    filesChecked = $targets.Count + $runtimeTargets.Count + 3
    rules = @($forbidden.Keys) + @($runtimeForbidden.Keys) + @(
        'Browser desktop sidebar width/min/max/flex-basis fixed at 240px',
        'Browser source Delete uses Windows SendToRecycleBin with no hard-delete fallback',
        'Browser source handler owns no direct file-delete API and crosses one injected recycleFile boundary',
        'WPF source Delete explicitly uses RecycleOption.SendToRecycleBin and UICancelOption.ThrowException with no DeletePermanently/File.Delete fallback'
    )
    message = 'Retired Browser/WPF UI controls remain absent; runtime has no active relative-date preset API or unchecked preset writer; Browser desktop sidebar width stays fixed; Browser and WPF source-image Delete remain Windows Recycle Bin-only. Managed Enhancement outputs and derived cache/temp cleanup remain separate ownership domains.'
} | ConvertTo-Json -Depth 4

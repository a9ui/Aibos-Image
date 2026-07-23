param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$wpfView = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.xaml'
$wpfRuntime = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.xaml.cs'
$findings = [Collections.Generic.List[object]]::new()

foreach ($target in @($wpfView, $wpfRuntime)) {
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
        throw "Required WPF source is missing: $target"
    }
}

$viewForbidden = [ordered]@{
    'retired Quick Search control' = '(?i)quick\s*search'
    'retired relative-date preset control' = '(?i)(Content\s*=\s*"\s*(Today|7d|30d|This year)\s*"|>\s*(Today|7d|30d|This year)\s*<)'
    'retired Favorite threshold label' = '(?i)(Lv|Favorite|笘・\*)\s*[1-5]\s*\+'
}
foreach ($rule in $viewForbidden.GetEnumerator()) {
    foreach ($match in @(Select-String -LiteralPath $wpfView -Pattern $rule.Value -AllMatches)) {
        $findings.Add([pscustomobject]@{
            Rule = $rule.Key
            File = [IO.Path]::GetRelativePath($repoRoot, $wpfView)
            Line = $match.LineNumber
            Text = $match.Line.Trim()
        })
    }
}

$runtimeForbidden = [ordered]@{
    'retired relative-date runtime preset API' = '(?i)(DatePresetTodayValue|DatePreset7DaysValue|DatePreset30DaysValue|DatePresetThisYearValue|DateRangeForPreset|SetDatePresetForSmoke)'
    'runtime state must not write an unchecked preset token' = 'DatePreset\s*=\s*_datePreset'
}
foreach ($rule in $runtimeForbidden.GetEnumerator()) {
    foreach ($match in @(Select-String -LiteralPath $wpfRuntime -Pattern $rule.Value -AllMatches)) {
        $findings.Add([pscustomobject]@{
            Rule = $rule.Key
            File = [IO.Path]::GetRelativePath($repoRoot, $wpfRuntime)
            Line = $match.LineNumber
            Text = $match.Line.Trim()
        })
    }
}

# Source-image Delete is a product safety boundary. Managed Enhancement outputs
# and derived cache/temp cleanup have separate ownership and are intentionally
# outside this source-delete contract.
$runtimeText = Get-Content -LiteralPath $wpfRuntime -Raw
$recycleStart = $runtimeText.IndexOf('private static RecycleBinDeleteResult SendFileToWindowsRecycleBin', [StringComparison]::Ordinal)
$recycleEnd = $runtimeText.IndexOf('private void SetStatusToast', [StringComparison]::Ordinal)
if ($recycleStart -lt 0 -or $recycleEnd -le $recycleStart) {
    $findings.Add([pscustomobject]@{
        Rule = 'WPF source Delete backend must remain a distinct Recycle Bin operation'
        File = [IO.Path]::GetRelativePath($repoRoot, $wpfRuntime)
        Line = 0
        Text = 'SendFileToWindowsRecycleBin production backend could not be isolated'
    })
}
else {
    $backend = $runtimeText.Substring($recycleStart, $recycleEnd - $recycleStart)
    if ($backend -notmatch 'Microsoft\.VisualBasic\.FileIO\.FileSystem\.DeleteFile\s*\(' `
        -or $backend -notmatch 'Microsoft\.VisualBasic\.FileIO\.RecycleOption\.SendToRecycleBin' `
        -or $backend -notmatch 'Microsoft\.VisualBasic\.FileIO\.UICancelOption\.ThrowException') {
        $findings.Add([pscustomobject]@{
            Rule = 'WPF source Delete backend must explicitly use RecycleOption.SendToRecycleBin and UICancelOption.ThrowException'
            File = [IO.Path]::GetRelativePath($repoRoot, $wpfRuntime)
            Line = 0
            Text = 'Required Microsoft.VisualBasic Recycle Bin call is missing'
        })
    }
    if ($backend -match '(?i)(DeletePermanently|\bFile\s*\.\s*Delete\s*\()') {
        $findings.Add([pscustomobject]@{
            Rule = 'WPF source Delete backend must not contain a permanent-delete fallback'
            File = [IO.Path]::GetRelativePath($repoRoot, $wpfRuntime)
            Line = 0
            Text = 'Hard-delete API found inside SendFileToWindowsRecycleBin'
        })
    }
}

if ($runtimeText -notmatch '_recycleBinDelete\s*=\s*SendFileToWindowsRecycleBin\s*;') {
    $findings.Add([pscustomobject]@{
        Rule = 'WPF source Delete workflow must default to the production Recycle Bin backend'
        File = [IO.Path]::GetRelativePath($repoRoot, $wpfRuntime)
        Line = 0
        Text = '_recycleBinDelete is not initialized with SendFileToWindowsRecycleBin'
    })
}

$workflowStart = $runtimeText.IndexOf('private bool ExecuteDelete', [StringComparison]::Ordinal)
$workflowEnd = $runtimeText.IndexOf('private bool TryValidateDelete', [StringComparison]::Ordinal)
if ($workflowStart -lt 0 -or $workflowEnd -le $workflowStart) {
    $findings.Add([pscustomobject]@{
        Rule = 'WPF single/bulk source Delete workflow must remain inspectable'
        File = [IO.Path]::GetRelativePath($repoRoot, $wpfRuntime)
        Line = 0
        Text = 'ExecuteDelete workflow could not be isolated'
    })
}
else {
    $workflow = $runtimeText.Substring($workflowStart, $workflowEnd - $workflowStart)
    if ($workflow -match '(?i)(DeletePermanently|\bFile\s*\.\s*Delete\s*\()') {
        $findings.Add([pscustomobject]@{
            Rule = 'WPF single/bulk source Delete workflow must not hard-delete files'
            File = [IO.Path]::GetRelativePath($repoRoot, $wpfRuntime)
            Line = 0
            Text = 'Hard-delete API found in the source Delete workflow'
        })
    }
}

if ($findings.Count -gt 0) {
    $findings | Format-Table -AutoSize | Out-String | Write-Host
    throw 'Aibos WPF UI or source-delete safety semantics regressed.'
}

[pscustomobject]@{
    ok = $true
    runtime = 'wpf'
    filesChecked = 2
    rules = @($viewForbidden.Keys) + @($runtimeForbidden.Keys) + @(
        'WPF source Delete explicitly uses RecycleOption.SendToRecycleBin and UICancelOption.ThrowException with no permanent-delete fallback'
    )
    message = 'Retired WPF controls remain absent and WPF source-image Delete remains Windows Recycle Bin-only.'
} | ConvertTo-Json -Depth 4

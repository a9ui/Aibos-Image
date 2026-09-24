# Read-only snapshot of one supported pinned desktop route. This binds actual
# shortcut/script bytes to the exact task plan; it grants no enrollment or
# external-lifetime authority and does not discover every possible launch entry.
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'CutoverManagedTask.ps1')

function Get-AibosCutoverDesktopScripts {
    # Fixed dependency inventory for the explicit pinned branch, not a caller's
    # arbitrary list. The observer itself is bound to invalidate changed readers.
    return @('scripts/request-aibos-desktop.ps1', 'scripts/start-aibos-desktop.ps1',
        'scripts/start-aibos-fixed-generation.ps1', 'scripts/start-wpf-detached.ps1',
        'scripts/check-wpf-launch-target.ps1', 'scripts/lib/DesktopActivation.ps1',
        'scripts/lib/CutoverManagedTask.ps1', 'scripts/lib/CutoverBootEvidence.ps1',
        'scripts/lib/CutoverDesktopGraph.ps1')
}

function Get-AibosCutoverFinalFilePath([IO.FileStream]$Stream) {
    if (-not ('AibosCutoverFilePath' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class AibosCutoverFilePath {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);
    public static string Read(SafeFileHandle file) {
        var path = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandleW(file, path, (uint)path.Capacity, 0);
        if (length == 0 || length >= path.Capacity) throw new IOException("Cannot resolve cutover file handle.");
        string value = path.ToString();
        if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + value.Substring(8);
        if (value.StartsWith(@"\\?\", StringComparison.Ordinal)) return value.Substring(4);
        throw new IOException("Unsupported cutover file path.");
    }
}
'@
    }
    return [AibosCutoverFilePath]::Read($Stream.SafeFileHandle)
}

function New-AibosCutoverDesktopGraph {
    param([string]$TaskName, [string]$LauncherRoot, [string]$ShortcutPath,
        [string]$PinnedLaunchManifestSha256, [string]$CompanionRoot = '')
    if ([string]::IsNullOrEmpty($PinnedLaunchManifestSha256)) { throw 'Desktop graph requires a pinned manifest.' }
    $root = [IO.Path]::GetFullPath($LauncherRoot)
    $shortcutPath = [IO.Path]::GetFullPath($ShortcutPath)
    if ([IO.Path]::GetExtension($shortcutPath) -ine '.lnk') { throw 'Desktop graph requires an existing shortcut.' }
    $expectedArguments = Get-AibosDesktopRequestArguments $root $TaskName $CompanionRoot -PinnedLaunchManifestSha256 $PinnedLaunchManifestSha256
    $configuration = Enter-AibosDesktopConfiguration
    $leases = [Collections.Generic.List[object]]::new()
    try {
        $names = @(Get-AibosCutoverDesktopScripts)
        $paths = @($shortcutPath) + @($names | ForEach-Object { [IO.Path]::GetFullPath((Join-Path $root $_)) })
        # A caller cannot declare arbitrary script bytes to be our supported
        # route. Match the selected tree to the observer's current package.
        $referenceRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
        $paths += @($names | ForEach-Object { [IO.Path]::GetFullPath((Join-Path $referenceRoot $_)) })
        foreach ($path in $paths) {
            $stream = [IO.FileStream]::new($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            $leases.Add([pscustomobject]@{ path=$path; stream=$stream })
            $limit = if ($path -ceq $shortcutPath) { 65536 } else { 1048576 }
            if ($stream.Length -lt 1 -or $stream.Length -gt $limit -or
                (Get-AibosCutoverFinalFilePath $stream) -ine $path -or
                (([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
                throw 'Unsupported, aliased or oversized desktop graph file.'
            }
        }
        $task = New-AibosCutoverTaskPlan $TaskName $root $CompanionRoot -PinnedLaunchManifestSha256 $PinnedLaunchManifestSha256
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $powerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        if ($shortcut.TargetPath -ine $powerShell -or $shortcut.Arguments -cne $expectedArguments -or
            $shortcut.WorkingDirectory.TrimEnd('\') -ine $root.TrimEnd('\') -or $shortcut.WindowStyle -ne 7 -or
            $shortcut.Description -cne 'Aibos Image - independent latest local Release launcher') {
            throw 'Shortcut does not resolve to the selected pinned desktop task.'
        }
        $files = @($leases | ForEach-Object {
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $digest = ([BitConverter]::ToString($sha.ComputeHash($_.stream))).Replace('-', '').ToLowerInvariant() }
            finally { $sha.Dispose() }
            if ((Get-AibosCutoverFinalFilePath $_.stream) -ine $_.path) { throw 'Desktop graph path changed during observation.' }
            [pscustomobject]@{ path=$_.path; sha256=$digest }
        })
        for ($index = 0; $index -lt $names.Count; $index++) {
            if ($files[$index + 1].sha256 -cne $files[$index + $names.Count + 1].sha256) {
                throw 'Selected launch scripts do not match the observer package.'
            }
        }
        # OS writers outside our mutex cannot be excluded; reject observed drift.
        $again = Read-AibosCutoverPlannedTask $task
        if ($again.Digest -cne $task.beforeSha256) { throw 'Task changed during graph observation.' }
        return [pscustomobject]@{ profile='aibos.pinned-desktop-graph/v1'; task=$task; files=@($files | Select-Object -First ($names.Count + 1)) }
    }
    finally {
        foreach ($lease in $leases) { $lease.stream.Dispose() }
        try { $configuration.ReleaseMutex() }
        finally { $configuration.Dispose() }
    }
}

function Assert-AibosCutoverDesktopGraph {
    param($Plan, [switch]$TaskClosed)
    if ($Plan.profile -cne 'aibos.pinned-desktop-graph/v1' -or @($Plan.files).Count -ne 10) { throw 'Unsupported desktop graph plan.' }
    $configuration = Enter-AibosDesktopConfiguration
    try {
        $current = New-AibosCutoverDesktopGraph $Plan.task.taskName $Plan.task.launcherRoot $Plan.files[0].path -PinnedLaunchManifestSha256 $Plan.task.pinnedLaunchManifestSha256 -CompanionRoot $Plan.task.companionRoot
        # Validate the saved task evidence as well as its current live definition.
        if ($TaskClosed) { Assert-AibosCutoverTaskClosed $Plan.task | Out-Null }
        else { Read-AibosCutoverPlannedTask $Plan.task | Out-Null }
        $expectedTask = if ($TaskClosed) { $Plan.task.closedSha256 } else { $Plan.task.beforeSha256 }
        if ($current.task.beforeSha256 -cne $expectedTask) { throw 'Desktop task no longer matches the saved graph phase.' }
        for ($index = 0; $index -lt $current.files.Count; $index++) {
            if ($Plan.files[$index].path -cne $current.files[$index].path -or
                $Plan.files[$index].sha256 -cne $current.files[$index].sha256) { throw 'Desktop graph file changed since preparation.' }
        }
        return [pscustomobject]@{ graphVerified=$true; taskClosed=[bool]$TaskClosed; cohortVerified=$false; maintenanceAllowed=$false }
    }
    finally {
        try { $configuration.ReleaseMutex() }
        finally { $configuration.Dispose() }
    }
}

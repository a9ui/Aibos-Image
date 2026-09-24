# Matches SingleInstanceCoordinator's versioned, per-user activation event.
# Opening an existing event never creates a primary or starts Enhancement.
function Get-AibosDesktopIdentitySuffix {
    param([Parameter(Mandatory)][string]$Identity)

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Identity))
        return ([BitConverter]::ToString($hash)).Replace('-', '').Substring(0, 24)
    }
    finally { $sha.Dispose() }

}

function Get-AibosDesktopTaskName {
    param([Parameter(Mandatory)][string]$Identity)
    return 'Aibos Image Desktop Launcher ' + (Get-AibosDesktopIdentitySuffix -Identity $Identity)
}

# One user's task and shortcut updates share a short transaction, including
# readback and rollback. Global binds the same user across Windows sessions;
# this is cooperative configuration exclusion, not application lifetime proof.
function Get-AibosDesktopConfigurationMutexName {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    return 'Global\AibosImage.DesktopConfiguration.v1.' + (Get-AibosDesktopIdentitySuffix $identity)
}

function Enter-AibosDesktopConfiguration {
    $mutex = [Threading.Mutex]::new($false, (Get-AibosDesktopConfigurationMutexName))
    try {
        $locked = $false
        try { $locked = $mutex.WaitOne([TimeSpan]::FromSeconds(3)) }
        catch [Threading.AbandonedMutexException] { $locked = $true }
        if (-not $locked) { throw 'Another Aibos desktop configuration change is in progress. Try again after it finishes.' }
        # Callers must read current state after entry, including abandonment.
        return $mutex
    }
    catch { $mutex.Dispose(); throw }
}

function Get-AibosDesktopActionArguments {
    param([Parameter(Mandatory)][string]$LauncherRoot, [string]$CompanionRoot = '', [switch]$AutoStartCompanion,
        [string]$PinnedLaunchManifestSha256 = '')
    if ($PinnedLaunchManifestSha256 -and ($PinnedLaunchManifestSha256.Length -ne 64 -or
        $PinnedLaunchManifestSha256 -cnotmatch '^[0-9A-F]{64}$' -or $AutoStartCompanion)) {
        throw 'Pinned desktop actions require a canonical Release manifest and passive startup.'
    }
    $runner = Join-Path ([IO.Path]::GetFullPath($LauncherRoot)) 'scripts\start-aibos-desktop.ps1'
    $companion = if ([string]::IsNullOrWhiteSpace($CompanionRoot)) { '' } else { [IO.Path]::GetFullPath($CompanionRoot) }
    foreach ($path in @($runner, $companion)) {
        if ($path.Contains('"') -or $path.Contains("`r") -or $path.Contains("`n")) { throw 'Invalid desktop launcher path.' }
    }
    $arguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}"' -f $runner
    if ($companion) {
        # Windows argv treats backslashes before the closing quote specially.
        # Preserve a directory's trailing separator instead of swallowing the
        # closing quote and the following AutoStartCompanion option.
        $quotedCompanion = [regex]::Replace($companion, '(\\+)$', '$1$1')
        $arguments += ' -CompanionRoot "{0}"' -f $quotedCompanion
    }
    if ($AutoStartCompanion) { $arguments += ' -AutoStartCompanion' }
    if ($PinnedLaunchManifestSha256) { $arguments += ' -PinnedLaunchManifestSha256 ' + $PinnedLaunchManifestSha256 }
    return $arguments
}

function Send-AibosDesktopActivation {
    param([Parameter(Mandatory)][string]$Identity)
    $suffix = Get-AibosDesktopIdentitySuffix -Identity $Identity
    $activation = $null
    try {
        $activation = [Threading.EventWaitHandle]::OpenExisting("Local\AibosImage.Wpf.SingleInstance.v1.Activate.$suffix")
        return $activation.Set()
    }
    catch [Threading.WaitHandleCannotBeOpenedException] { return $false }
    finally { if ($null -ne $activation) { $activation.Dispose() } }
}

function Get-AibosDesktopRequestArguments {
    param([string]$LauncherRoot, [string]$TaskName, [string]$CompanionRoot = '', [string]$PinnedLaunchManifestSha256 = '')
    if ([string]::IsNullOrWhiteSpace($TaskName) -or $TaskName.Length -gt 64 -or
        $TaskName -notmatch '^[\p{L}\p{N} ._-]+$') { throw 'Invalid desktop task name.' }
    # Share path/manifest validation with the action formatter.
    Get-AibosDesktopActionArguments $LauncherRoot $CompanionRoot -PinnedLaunchManifestSha256 $PinnedLaunchManifestSha256 | Out-Null
    $request = Join-Path ([IO.Path]::GetFullPath($LauncherRoot)) 'scripts\request-aibos-desktop.ps1'
    $arguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}" -TaskName "{1}"' -f $request, $TaskName
    if ($PinnedLaunchManifestSha256) {
        $arguments += ' -PinnedLaunchManifestSha256 ' + $PinnedLaunchManifestSha256
        if (-not [string]::IsNullOrWhiteSpace($CompanionRoot)) {
            $quoted = [regex]::Replace([IO.Path]::GetFullPath($CompanionRoot), '(\\+)$', '$1$1')
            $arguments += ' -CompanionRoot "{0}"' -f $quoted
        }
    }
    return $arguments
}

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Low')]
param(
    [string]$CompanionRoot = '',
    [string]$ShortcutPath = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Aibos Image.lnk'),
    [string]$TaskName = 'Aibos Image Desktop Launcher',
    [switch]$PassThru
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($TaskName) -or
    $TaskName.Length -gt 64 -or
    $TaskName -notmatch '^[\p{L}\p{N} ._-]+$') {
    throw 'TaskName must contain only letters, numbers, spaces, dots, underscores, or hyphens and be at most 64 characters.'
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$desktopRunner = Join-Path $repoRoot 'scripts\start-aibos-desktop.ps1'
$target = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\bin\Release\net10.0-windows\PhotoViewer.Wpf.exe'
$powerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$resolvedShortcutPath = [IO.Path]::GetFullPath($ShortcutPath)
$shortcutDirectory = Split-Path -Parent $resolvedShortcutPath

if ([IO.Path]::GetExtension($resolvedShortcutPath) -ine '.lnk') {
    throw 'ShortcutPath must end in .lnk.'
}
if (-not (Test-Path -LiteralPath $desktopRunner -PathType Leaf)) {
    throw "Desktop runner was not found: $desktopRunner"
}
if (-not (Test-Path -LiteralPath $powerShell -PathType Leaf)) {
    throw "Windows PowerShell was not found: $powerShell"
}
if (-not (Test-Path -LiteralPath $shortcutDirectory -PathType Container)) {
    throw "Shortcut directory was not found: $shortcutDirectory"
}

$resolvedCompanionRoot = ''
if (-not [string]::IsNullOrWhiteSpace($CompanionRoot)) {
    $resolvedCompanionRoot = [IO.Path]::GetFullPath($CompanionRoot)
    $companionLauncher = Join-Path $resolvedCompanionRoot 'scripts\enhancement_companion.js'
    if (-not (Test-Path -LiteralPath $companionLauncher -PathType Leaf)) {
        throw 'The explicitly selected Companion root is invalid.'
    }
}

foreach ($value in @($desktopRunner, $resolvedCompanionRoot)) {
    if ($value.Contains('"')) {
        throw 'Launcher paths must not contain a double quote.'
    }
}

$actionArguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}"' -f $desktopRunner
if (-not [string]::IsNullOrWhiteSpace($resolvedCompanionRoot)) {
    $actionArguments += ' -CompanionRoot "{0}"' -f $resolvedCompanionRoot
}

$userId = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$action = New-ScheduledTaskAction `
    -Execute $powerShell `
    -Argument $actionArguments `
    -WorkingDirectory $repoRoot
$principal = New-ScheduledTaskPrincipal `
    -UserId $userId `
    -LogonType Interactive `
    -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew
$task = New-ScheduledTask `
    -Action $action `
    -Principal $principal `
    -Settings $settings `
    -Description 'Starts Aibos Image independently from the process that requested the launch.'

$shortcutArguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command "Start-ScheduledTask -TaskName ''{0}''"' -f $TaskName
$temporaryShortcut = Join-Path $shortcutDirectory ('.aibos-shortcut-{0}.lnk' -f [guid]::NewGuid().ToString('N'))

try {
    if ($PSCmdlet.ShouldProcess($TaskName, 'Register independent Aibos desktop launcher task')) {
        Register-ScheduledTask -TaskName $TaskName -InputObject $task -Force | Out-Null
    }

    if ($PSCmdlet.ShouldProcess($resolvedShortcutPath, 'Create Aibos Image desktop shortcut')) {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($temporaryShortcut)
        $shortcut.TargetPath = $powerShell
        $shortcut.Arguments = $shortcutArguments
        $shortcut.WorkingDirectory = $repoRoot
        $shortcut.WindowStyle = 7
        $shortcut.Description = 'Aibos Image - independent latest local Release launcher'
        if (Test-Path -LiteralPath $target -PathType Leaf) {
            $shortcut.IconLocation = "$target,0"
        }
        $shortcut.Save()
        Move-Item -LiteralPath $temporaryShortcut -Destination $resolvedShortcutPath -Force
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryShortcut -PathType Leaf) {
        Remove-Item -LiteralPath $temporaryShortcut -Force
    }
}

if ($PassThru) {
    [pscustomobject]@{
        TaskName = $TaskName
        UserId = $userId
        Action = $powerShell
        ActionArguments = $actionArguments
        WorkingDirectory = $repoRoot
        MultipleInstances = 'IgnoreNew'
        ExecutionTimeLimit = [TimeSpan]::Zero
        ShortcutPath = $resolvedShortcutPath
        ShortcutTarget = $powerShell
        ShortcutArguments = $shortcutArguments
    }
}

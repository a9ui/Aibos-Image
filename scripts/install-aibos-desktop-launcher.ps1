[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Low')]
param(
    [string]$CompanionRoot = '',
    [switch]$AutoStartCompanion,
    [string]$ShortcutPath = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Aibos Image.lnk'),
    [string]$TaskName = '',
    [switch]$PassThru
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\DesktopActivation.ps1')
if ([string]::IsNullOrEmpty($TaskName)) {
    $TaskName = Get-AibosDesktopTaskName -Identity ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
}

if ([string]::IsNullOrWhiteSpace($TaskName) -or
    $TaskName.Length -gt 64 -or
    $TaskName -notmatch '^[\p{L}\p{N} ._-]+$') {
    throw 'TaskName must contain only letters, numbers, spaces, dots, underscores, or hyphens and be at most 64 characters.'
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$desktopRunner = Join-Path $repoRoot 'scripts\start-aibos-desktop.ps1'
$desktopRequest = Join-Path $repoRoot 'scripts\request-aibos-desktop.ps1'
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
if (-not (Test-Path -LiteralPath $desktopRequest -PathType Leaf)) {
    throw "Desktop request script was not found: $desktopRequest"
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

foreach ($value in @($desktopRunner, $desktopRequest, $resolvedCompanionRoot)) {
    if ($value.Contains('"')) {
        throw 'Launcher paths must not contain a double quote.'
    }
}

$actionArguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}"' -f $desktopRunner
if (-not [string]::IsNullOrWhiteSpace($resolvedCompanionRoot)) {
    $actionArguments += ' -CompanionRoot "{0}"' -f $resolvedCompanionRoot
}

if ($AutoStartCompanion) { $actionArguments += ' -AutoStartCompanion' }
$userId = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$description = 'Starts Aibos Image independently from the process that requested the launch.'
$existing = @(Get-ScheduledTask -TaskPath '\' -ErrorAction Stop | Where-Object { $_.TaskName -eq $TaskName })
if ($existing.Count -gt 1) { throw 'The desktop task identity is ambiguous.' }
if ($existing.Count -eq 1) {
    $ownerName = $existing[0].Principal.UserId
    $ownerSid = if ($ownerName -match '^S-1-') {
        [Security.Principal.SecurityIdentifier]::new($ownerName).Value
    } else {
        [Security.Principal.NTAccount]::new($ownerName).Translate([Security.Principal.SecurityIdentifier]).Value
    }
    if ($existing[0].Description -ne $description -or
        $ownerSid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -or
        @($existing[0].Actions).Count -ne 1 -or
        $existing[0].Actions[0].Execute -ine $powerShell -or
        $existing[0].Actions[0].Arguments -notmatch ' -File "[^"\r\n]+\\scripts\\start-aibos-desktop\.ps1"(?: -CompanionRoot "[^"\r\n]+")?(?: -AutoStartCompanion)?$') {
        throw 'The selected task belongs to another launcher. Choose a different TaskName.'
    }
}
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
    -MultipleInstances Parallel
$task = New-ScheduledTask `
    -Action $action `
    -Principal $principal `
    -Settings $settings `
    -Description $description

$shortcutArguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}" -TaskName "{1}"' -f $desktopRequest, $TaskName
$temporaryShortcut = Join-Path $shortcutDirectory ('.aibos-shortcut-{0}.lnk' -f [guid]::NewGuid().ToString('N'))

$registered = $false
$previousXml = $null
try {
    if ($PSCmdlet.ShouldProcess("$TaskName -> $resolvedShortcutPath", 'Install independent Aibos desktop launcher and shortcut')) {
        if ($existing.Count -eq 1) {
            $previousXml = Export-ScheduledTask -TaskPath '\' -TaskName $TaskName -ErrorAction Stop
        }
        $shell = New-Object -ComObject WScript.Shell
        if (Test-Path -LiteralPath $resolvedShortcutPath) {
            $oldShortcut = $shell.CreateShortcut($resolvedShortcutPath)
            if ($oldShortcut.Description -ne 'Aibos Image - independent latest local Release launcher') {
                throw 'The shortcut path is already in use. Choose a different ShortcutPath.'
            }
        }
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
        Register-ScheduledTask -TaskPath '\' -TaskName $TaskName -InputObject $task -Force -ErrorAction Stop | Out-Null
        $registered = $true
        if (Test-Path -LiteralPath $resolvedShortcutPath -PathType Leaf) {
            [IO.File]::Replace($temporaryShortcut, $resolvedShortcutPath, [NullString]::Value)
        } else {
            [IO.File]::Move($temporaryShortcut, $resolvedShortcutPath)
        }
    }
}
catch {
    $failure = $_
    if ($registered) {
        if ($null -ne $previousXml) {
            Register-ScheduledTask -TaskPath '\' -TaskName $TaskName -Xml $previousXml -Force -ErrorAction Stop | Out-Null
        } else {
            Unregister-ScheduledTask -TaskPath '\' -TaskName $TaskName -Confirm:$false -ErrorAction Stop
        }
    }
    throw $failure
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
        MultipleInstances = 'Parallel'
        ExecutionTimeLimit = [TimeSpan]::Zero
        ShortcutPath = $resolvedShortcutPath
        ShortcutTarget = $powerShell
        ShortcutArguments = $shortcutArguments
    }
}

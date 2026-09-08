[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Load the constructors before defining mocks. Windows PowerShell module
# auto-loading can otherwise replace the mocked scheduler commands mid-test.
Import-Module ScheduledTasks -ErrorAction Stop
$installer = Join-Path $PSScriptRoot 'install-aibos-desktop-launcher.ps1'
. (Join-Path $PSScriptRoot 'lib\DesktopActivation.ps1')
if ((Get-AibosDesktopTaskName 'S-1-5-21-111-1001') -eq (Get-AibosDesktopTaskName 'S-1-5-21-111-1002')) {
    throw 'Different synthetic users shared a default task name.'
}
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('aibos-install-verifier-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
$shortcutPath = Join-Path $runRoot 'Synthetic Aibos.lnk'
$taskName = 'Aibos Synthetic Install'
$installProbe = @{}
$installProbe.existingTask = $null
$installProbe.registeredTask = $null
$installProbe.registerCalls = 0
$installProbe.unregisterCalls = 0
$installProbe.failRegistration = $false
$installProbe.restoredXml = $false

# Scheduler writes are mocked; COM shortcut publication uses only this TEMP root.
function Get-ScheduledTask { [CmdletBinding()] param($TaskPath) if ($null -ne $installProbe.existingTask) { $installProbe.existingTask } }
function Export-ScheduledTask { [CmdletBinding()] param($TaskPath, $TaskName) '<Task>synthetic-prior-definition</Task>' }
function Register-ScheduledTask {
    [CmdletBinding()] param($TaskPath, $TaskName, $InputObject, $Xml, [switch]$Force)
    $installProbe.registerCalls++
    if ($installProbe.failRegistration) { Write-Error 'Synthetic registration failure'; return }
    if ($Xml) { $installProbe.restoredXml = $true } else { $installProbe.registeredTask = $InputObject }
}
function Unregister-ScheduledTask {
    [CmdletBinding(SupportsShouldProcess)] param($TaskPath, $TaskName)
    $installProbe.unregisterCalls++
}
function Install { & $installer -TaskName $taskName -ShortcutPath $shortcutPath @args }
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }

Install
Assert-True ($installProbe.registerCalls -eq 1) 'Fresh install did not register exactly once.'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
Assert-True ($shortcut.Arguments -match 'request-aibos-desktop\.ps1') 'Shortcut does not use the activation dispatcher.'
$installProbe.existingTask = $installProbe.registeredTask
$installProbe.existingTask | Add-Member -NotePropertyName TaskName -NotePropertyValue $taskName -Force
Install
Assert-True ($installProbe.registerCalls -eq 2) 'Owned reinstallation failed.'
$hashBefore = (Get-FileHash -LiteralPath $shortcutPath).Hash
$callsBefore = $installProbe.registerCalls
Install -WhatIf
Assert-True ($installProbe.registerCalls -eq $callsBefore) 'WhatIf registered a task.'
Assert-True ((Get-FileHash -LiteralPath $shortcutPath).Hash -eq $hashBefore) 'WhatIf changed the shortcut.'

$ownedDescription = $installProbe.existingTask.Description
$installProbe.existingTask.Description = 'Unrelated task'
$rejected = $false
try { Install } catch { $rejected = $_.Exception.Message -like '*another launcher*' }
Assert-True $rejected 'An unrelated scheduled task was overwritten.'
Assert-True ($installProbe.registerCalls -eq $callsBefore) 'Collision rejection registered a task.'
$installProbe.existingTask.Description = $ownedDescription

$installProbe.failRegistration = $true
$rejected = $false
try { Install } catch { $rejected = $_.Exception.Message -like '*Synthetic registration failure*' }
Assert-True $rejected 'A registration failure was ignored.'
Assert-True ((Get-FileHash -LiteralPath $shortcutPath).Hash -eq $hashBefore) 'Failed registration changed the shortcut.'
$installProbe.failRegistration = $false

$lock = [IO.File]::Open($shortcutPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    $rejected = $false
    try { Install } catch { $rejected = $true }
    Assert-True $rejected 'Shortcut publication failure was ignored.'
    Assert-True $installProbe.restoredXml 'The prior task definition was not restored.'
    $installProbe.existingTask = $null
    try { Install } catch { }
    Assert-True ($installProbe.unregisterCalls -eq 1) 'The new task was not rolled back after shortcut failure.'
}
finally { $lock.Dispose() }
Assert-True ((Get-FileHash -LiteralPath $shortcutPath).Hash -eq $hashBefore) 'Rollback changed the prior shortcut.'
Assert-True (@(Get-ChildItem -LiteralPath $runRoot -Filter '.aibos-shortcut-*.lnk').Count -eq 0) 'A staged shortcut was leaked.'
[pscustomobject]@{ ok = $true; freshInstall = $true; ownedUpdate = $true; whatIf = $true; collisionRejected = $true; registrationFailurePreservedShortcut = $true; replacementRollback = $true; schedulerWritesMocked = $true; fixtureRoot = $runRoot } | ConvertTo-Json

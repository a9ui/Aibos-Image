[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Load the constructors before defining mocks. Windows PowerShell module
# auto-loading can otherwise replace the mocked scheduler commands mid-test.
Import-Module ScheduledTasks -ErrorAction Stop
$installer = Join-Path $PSScriptRoot 'install-aibos-desktop-launcher.ps1'
. (Join-Path $PSScriptRoot 'lib\DesktopActivation.ps1')
. (Join-Path $PSScriptRoot 'lib\CutoverManagedTask.ps1')
# Use another OS thread: a same-thread WaitOne would reenter a named Mutex
# and could not prove exclusion around scheduler readback and rollback.
Add-Type -TypeDefinition @'
using System;
using System.Threading;
public sealed class AibosConfigurationLockFixture : IDisposable {
    private readonly ManualResetEventSlim ready = new ManualResetEventSlim();
    private readonly ManualResetEventSlim release = new ManualResetEventSlim();
    private readonly Thread thread;
    public AibosConfigurationLockFixture(string name) {
        thread = new Thread(() => {
            using (var mutex = new Mutex(false, name)) {
                bool held = false;
                try {
                    try { held = mutex.WaitOne(3000); } catch (AbandonedMutexException) { held = true; }
                    if (!held) return;
                    ready.Set(); release.Wait(20000);
                } finally { if (held) mutex.ReleaseMutex(); }
            }
        });
        thread.IsBackground = true; thread.Start();
        if (!ready.Wait(4000)) { Dispose(); throw new Exception("Fixture lock not acquired."); }
    }
    public static bool CanAcquire(string name) {
        bool acquired = false;
        var probe = new Thread(() => {
            using (var mutex = new Mutex(false, name)) {
                try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (acquired) mutex.ReleaseMutex();
            }
        });
        probe.IsBackground = true; probe.Start();
        if (!probe.Join(3000)) throw new Exception("Fixture lock probe did not exit.");
        return acquired;
    }
    public void Dispose() {
        release.Set();
        if (!thread.Join(4000)) throw new Exception("Fixture lock owner did not exit.");
        ready.Dispose(); release.Dispose();
    }
}
'@
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
$installProbe.registrationHappened = $false
$installProbe.readbackFault = ''
$installProbe.priorTask = $null
$installProbe.schedulerReads = 0
$installProbe.verifyExclusion = $false

function Assert-ConfigurationExclusion {
    if ($installProbe.verifyExclusion -and [AibosConfigurationLockFixture]::CanAcquire((Get-AibosDesktopConfigurationMutexName))) {
        throw 'Scheduler access escaped the desktop configuration transaction.'
    }
}

# Scheduler writes are mocked; COM shortcut publication uses only this TEMP root.
function Get-ScheduledTask {
    [CmdletBinding()] param($TaskPath)
    Assert-ConfigurationExclusion
    $installProbe.schedulerReads++
    if ($installProbe.registrationHappened) {
        if ($installProbe.readbackFault -eq 'missing') { return }
        if ($installProbe.readbackFault -eq 'unreadable') { throw 'Synthetic task readback failure' }
        if ($installProbe.readbackFault -eq 'arguments') {
            $installProbe.existingTask.Actions[0].Arguments += ' -Unexpected'
        }
        if ($installProbe.readbackFault -eq 'disabled') {
            $installProbe.existingTask.Settings.Enabled = $false
        }
        if ($installProbe.readbackFault -eq 'duplicate') { $installProbe.existingTask }
    }
    if ($null -ne $installProbe.existingTask) { $installProbe.existingTask }
}
function Export-ScheduledTask {
    [CmdletBinding()] param($TaskPath, $TaskName)
    Assert-ConfigurationExclusion
    $installProbe.schedulerReads++
    $installProbe.priorTask = $installProbe.existingTask
    '<Task>synthetic-prior-definition</Task>'
}
function Register-ScheduledTask {
    [CmdletBinding()] param($TaskPath, $TaskName, $InputObject, $Xml, [switch]$Force)
    Assert-ConfigurationExclusion
    $installProbe.registerCalls++
    if ($installProbe.failRegistration) { Write-Error 'Synthetic registration failure'; return }
    if ($Xml) {
        $installProbe.restoredXml = $true
        $installProbe.existingTask = $installProbe.priorTask
    } else {
        $InputObject | Add-Member -NotePropertyName TaskName -NotePropertyValue $TaskName -Force
        $installProbe.registeredTask = $InputObject
        $installProbe.existingTask = $InputObject
        $installProbe.registrationHappened = $true
    }
}
function Unregister-ScheduledTask {
    [CmdletBinding(SupportsShouldProcess)] param($TaskPath, $TaskName)
    Assert-ConfigurationExclusion
    $installProbe.unregisterCalls++
    $installProbe.existingTask = $null
}
function Install {
    $installProbe.registrationHappened = $false
    & $installer -TaskName $taskName -ShortcutPath $shortcutPath @args
}
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }

$holder = [AibosConfigurationLockFixture]::new((Get-AibosDesktopConfigurationMutexName))
try {
    $blocked = $false
    try { Install } catch { $blocked = $_.Exception.Message -like '*configuration change is in progress*' }
    Assert-True $blocked 'Installer entered a concurrent configuration transaction.'
    $blocked = $false
    try { Close-AibosCutoverPlannedTask ([pscustomobject]@{}) } catch { $blocked = $_.Exception.Message -like '*configuration change is in progress*' }
    Assert-True $blocked 'Task closure entered a concurrent configuration transaction.'
    Assert-True ($installProbe.schedulerReads -eq 0 -and $installProbe.registerCalls -eq 0) 'Blocked configuration touched the scheduler.'
}
finally { $holder.Dispose() }
$installProbe.verifyExclusion = $true

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

foreach ($fault in @('missing', 'duplicate', 'arguments', 'disabled', 'unreadable')) {
    $installProbe.readbackFault = $fault
    $installProbe.restoredXml = $false
    $beforeTask = $installProbe.existingTask
    $rejected = $false
    try { Install } catch { $rejected = $true }
    Assert-True $rejected "Task readback fault $fault was ignored."
    Assert-True $installProbe.restoredXml "Task readback fault $fault did not restore the prior definition."
    Assert-True ([Object]::ReferenceEquals($beforeTask, $installProbe.existingTask)) 'Rollback lost the prior task.'
    Assert-True ((Get-FileHash -LiteralPath $shortcutPath).Hash -eq $hashBefore) 'Task readback failure replaced the prior shortcut.'
}
$installProbe.readbackFault = ''

$ownedTask = $installProbe.existingTask
$installProbe.existingTask = $null
$installProbe.readbackFault = 'missing'
$unregistersBefore = $installProbe.unregisterCalls
$rejected = $false
try { Install } catch { $rejected = $true }
Assert-True $rejected 'Fresh task readback failure was ignored.'
Assert-True ($installProbe.unregisterCalls -eq $unregistersBefore + 1) 'Failed fresh task readback did not remove the new registration.'
Assert-True ((Get-FileHash -LiteralPath $shortcutPath).Hash -eq $hashBefore) 'Failed fresh task readback replaced the shortcut.'
$installProbe.existingTask = $ownedTask
$installProbe.readbackFault = ''
$unregistersBefore = $installProbe.unregisterCalls

$lock = [IO.File]::Open($shortcutPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    $rejected = $false
    try { Install } catch { $rejected = $true }
    Assert-True $rejected 'Shortcut publication failure was ignored.'
    Assert-True $installProbe.restoredXml 'The prior task definition was not restored.'
    $installProbe.existingTask = $null
    try { Install } catch { }
    Assert-True ($installProbe.unregisterCalls -eq $unregistersBefore + 1) 'The new task was not rolled back after shortcut failure.'
}
finally { $lock.Dispose() }
Assert-True ((Get-FileHash -LiteralPath $shortcutPath).Hash -eq $hashBefore) 'Rollback changed the prior shortcut.'
Assert-True (@(Get-ChildItem -LiteralPath $runRoot -Filter '.aibos-shortcut-*.lnk').Count -eq 0) 'A staged shortcut was leaked.'
# Reinstallation must still recognize its owned task after the argument
# formatter escapes an explicit Companion directory's trailing separator.
$companionRoot = Join-Path $runRoot 'Synthetic Companion'
New-Item -ItemType Directory -Path (Join-Path $companionRoot 'scripts') | Out-Null
[IO.File]::WriteAllText((Join-Path $companionRoot 'scripts\enhancement_companion.js'), '// Synthetic existence marker only.', [Text.UTF8Encoding]::new($false))
Install -CompanionRoot ($companionRoot + '\') -AutoStartCompanion
Install -CompanionRoot ($companionRoot + '\') -AutoStartCompanion
$manifest = 'A' * 64
Install -CompanionRoot ($companionRoot + '\') -PinnedLaunchManifestSha256 $manifest
$pinnedTaskArguments = $installProbe.registeredTask.Actions[0].Arguments
$pinnedShortcut = $shell.CreateShortcut($shortcutPath)
Assert-True ($pinnedTaskArguments.EndsWith(' -PinnedLaunchManifestSha256 ' + $manifest)) 'Pinned task lost its selected manifest.'
Assert-True ($pinnedShortcut.Arguments.Contains(' -PinnedLaunchManifestSha256 ' + $manifest)) 'Pinned shortcut reverted to ordinary activation.'
Assert-True ($pinnedShortcut.Arguments.Contains(' -CompanionRoot "' + $companionRoot + '\\"')) 'Pinned shortcut lost the selected Companion argument boundary.'
Install -CompanionRoot ($companionRoot + '\') -PinnedLaunchManifestSha256 $manifest
$callsBefore = $installProbe.registerCalls
$readsBefore = $installProbe.schedulerReads
foreach ($invalid in @('', 'invalid', ('a' * 64))) {
    $rejected = $false
    try { Install -PinnedLaunchManifestSha256 $invalid } catch { $rejected = $true }
    Assert-True $rejected 'Invalid pinned configuration was installed.'
}
$rejected = $false
try { Install -PinnedLaunchManifestSha256 $manifest -AutoStartCompanion } catch { $rejected = $true }
Assert-True $rejected 'Pinned configuration allowed automatic Companion startup.'
Assert-True ($installProbe.registerCalls -eq $callsBefore -and $installProbe.schedulerReads -eq $readsBefore) 'Invalid pinned configuration touched the scheduler.'
Install
Assert-True (-not $installProbe.registeredTask.Actions[0].Arguments.Contains('PinnedLaunchManifest')) 'Explicit normal reinstallation did not replace pinned configuration.'
& (Join-Path $PSScriptRoot 'verify-desktop-launch-arguments.ps1')
Assert-True ([AibosConfigurationLockFixture]::CanAcquire((Get-AibosDesktopConfigurationMutexName))) 'Configuration exclusion leaked after failure or successful installation.'
[pscustomobject]@{ ok = $true; configurationExclusion = $true; freshInstall = $true; ownedUpdate = $true; pinnedConfiguration = $true; whatIf = $true; collisionRejected = $true; registrationFailurePreservedShortcut = $true; readbackFaultsRejected = 6; replacementRollback = $true; schedulerWritesMocked = $true; fixtureRoot = $runRoot } | ConvertTo-Json

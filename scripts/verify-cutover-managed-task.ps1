[CmdletBinding()]
param([string]$PlanOutputPath = '', [string]$SavedIntentPath = '')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\CutoverManagedTask.ps1')
$script:checks = 0
$script:disableCalls = 0
$script:disableMode = 'normal'
$script:exportFailure = $false
$script:taskName = 'Aibos Synthetic Cutover'
$root = Join-Path ([IO.Path]::GetTempPath()) 'Aibos Synthetic Launcher'
$owner = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$exe = [Security.SecurityElement]::Escape((Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'))
$arguments = [Security.SecurityElement]::Escape((Get-AibosDesktopActionArguments $root))
$escapedRoot = [Security.SecurityElement]::Escape($root)
$script:originalXml = @"
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>Starts Aibos Image independently from the process that requested the launch.</Description></RegistrationInfo>
  <Triggers />
  <Principals><Principal id="Author"><UserId>$owner</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings><MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy><Enabled>true</Enabled><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Hidden>false</Hidden></Settings>
  <Actions Context="Author"><Exec><Command>$exe</Command><Arguments>$arguments</Arguments><WorkingDirectory>$escapedRoot</WorkingDirectory></Exec></Actions>
</Task>
"@
$script:xml = $script:originalXml

# Both native scheduler boundaries are mocked. Any accidentally introduced
# start/stop/register/enable operation fails rather than touching the host.
function Export-ScheduledTask {
    [CmdletBinding()] param($TaskPath, $TaskName)
    if ($TaskPath -cne '\' -or $TaskName -cne $script:taskName) { throw 'Fixture rejected unbound scheduler read.' }
    if ($script:exportFailure) { throw 'Synthetic scheduler read failure.' }
    return $script:xml
}
function Disable-ScheduledTask {
    [CmdletBinding()] param($TaskPath, $TaskName)
    if ($TaskPath -cne '\' -or $TaskName -cne $script:taskName) { throw 'Fixture rejected unbound scheduler write.' }
    $script:disableCalls++
    if ($script:disableMode -eq 'before-failure') { throw 'Synthetic disable failure.' }
    if ($script:disableMode -eq 'noop') { return }
    $script:xml = $script:xml.Replace('<Enabled>true</Enabled>', '<Enabled>false</Enabled>')
    if ($script:disableMode -eq 'after-failure') { throw 'Synthetic lost disable response.' }
    if ($script:disableMode -eq 'drift') { $script:xml = $script:xml.Replace('<Hidden>false</Hidden>', '<Hidden>true</Hidden>') }
    if ($script:disableMode -eq 'read-failure') { $script:exportFailure = $true }
}
function Start-ScheduledTask { throw 'Forbidden task start.' }
function Stop-ScheduledTask { throw 'Forbidden process stop.' }
function Enable-ScheduledTask { throw 'Forbidden automatic rollback.' }
function Register-ScheduledTask { throw 'Forbidden task replacement.' }
function Unregister-ScheduledTask { throw 'Forbidden task removal.' }
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }; $script:checks++
}
function Refuses([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    Check $failed $Message
}

if ($SavedIntentPath) {
    $saved = Get-Content -LiteralPath $SavedIntentPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $plan = $saved.configuration.managedEntries[0]
    $script:taskName = $plan.taskName
    $script:xml = $plan.beforeXml
    $result = Close-AibosCutoverPlannedTask $plan
    $observed = Assert-AibosCutoverTaskClosed $plan
    Check ($result.closed -and $result.changed -and -not $observed.cohortVerified -and $script:disableCalls -eq 1) 'Saved plan could not drive the actual closure adapter against the mocked scheduler.'
    [pscustomobject]@{ savedIntentToTaskAdapter=$true; schedulerWritesMocked=$true; cohortVerified=$false } | ConvertTo-Json
    return
}

$plan = New-AibosCutoverTaskPlan $script:taskName $root
Check ($script:disableCalls -eq 0 -and $plan.beforeSha256 -cne $plan.closedSha256) 'Preparation changed scheduler state or did not distinguish closure.'
if ($PlanOutputPath) {
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($PlanOutputPath), ($plan | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
}
$result = Close-AibosCutoverPlannedTask $plan
Check ($result.closed -and $result.changed -and -not $result.cohortVerified -and $script:disableCalls -eq 1) 'Closure failed or granted cohort authority.'
$result = Close-AibosCutoverPlannedTask $plan
Check ($result.closed -and -not $result.changed -and $script:disableCalls -eq 1) 'Replay issued another disable instead of reading the closed task.'
Assert-AibosCutoverTaskClosed $plan | Out-Null
$script:xml = $script:originalXml
Refuses { Assert-AibosCutoverTaskClosed $plan } 'A reopened task retained closure evidence.'
Check ($script:disableCalls -eq 1 -and $script:xml.Contains('<Enabled>true</Enabled>')) 'Read-only assertion silently reclosed a reopened task.'

foreach ($mutation in @(
    @{ From=$owner; To='S-1-5-18' },
    @{ From='LeastPrivilege'; To='HighestAvailable' },
    @{ From='InteractiveToken'; To='ServiceAccount' },
    @{ From='Starts Aibos Image independently from the process that requested the launch.'; To='Unrelated task' },
    @{ From='-NoProfile'; To='-NoProfile -Command unexpected' },
    @{ From='<Triggers />'; To='<Triggers><BootTrigger /></Triggers>' },
    @{ From='version="1.4"'; To='version="999"' },
    @{ From='<Enabled>true</Enabled>'; To='<Enabled>unknown</Enabled>' },
    @{ From='<Enabled>true</Enabled>'; To='' },
    @{ From='<Hidden>false</Hidden>'; To='<Hidden>true</Hidden>' },
    @{ From='Context="Author"'; To='Context="Different"' }
)) {
    $script:xml = $script:originalXml.Replace($mutation.From, $mutation.To)
    $before = $script:disableCalls
    Refuses { Close-AibosCutoverPlannedTask $plan } 'Changed or unsupported task was closed.'
    Check ($script:disableCalls -eq $before) 'Invalid task caused a scheduler mutation.'
}
$script:xml = '<!DOCTYPE Task [<!ENTITY value "unexpected">]>' + $script:originalXml
Refuses { Close-AibosCutoverPlannedTask $plan } 'DTD was accepted.'
$script:xml = $script:originalXml + (' ' * 65536)
Refuses { Close-AibosCutoverPlannedTask $plan } 'Oversized scheduler definition was accepted.'
$script:xml = $script:originalXml
Refuses { New-AibosCutoverTaskPlan '*' $root } 'Wildcard task inventory was permitted.'
$badPlan = $plan.PSObject.Copy(); $badPlan.beforeXml += ' '
Refuses { Close-AibosCutoverPlannedTask $badPlan } 'Changed saved XML was accepted.'

foreach ($mode in @('before-failure', 'noop', 'after-failure', 'drift', 'read-failure')) {
    $script:xml = $script:originalXml; $script:disableMode = $mode; $script:exportFailure = $false
    $before = $script:disableCalls
    Refuses { Close-AibosCutoverPlannedTask $plan } 'Failed closure/readback was reported as successful.'
    Check ($script:disableCalls -eq $before + 1) 'Failure handling repeated a mutation.'
    if ($mode -in @('after-failure', 'drift', 'read-failure')) {
        Check ($script:xml.Contains('<Enabled>false</Enabled>')) 'Failure handling reopened the task.'
    }
    if ($mode -eq 'after-failure') {
        $script:disableMode = 'normal'
        $result = Close-AibosCutoverPlannedTask $plan
        Check ($result.closed -and -not $result.changed -and $script:disableCalls -eq $before + 1) 'Lost-response replay disabled the task again.'
    }
}
$script:disableMode = 'normal'; $script:exportFailure = $false
$script:xml = $script:originalXml.Replace('<Enabled>true</Enabled>', '<Enabled>false</Enabled>')
$disabledPlan = New-AibosCutoverTaskPlan $script:taskName $root
$before = $script:disableCalls
$result = Close-AibosCutoverPlannedTask $disabledPlan
Check ($result.closed -and -not $result.changed -and $script:disableCalls -eq $before) 'Already-disabled preparation caused a write.'

# A saved optional Companion path must not acquire a different meaning when
# the Arm process starts with a different current directory.
$companionArguments = [Security.SecurityElement]::Escape((Get-AibosDesktopActionArguments $root 'Synthetic Companion' -AutoStartCompanion))
$script:xml = $script:originalXml.Replace($arguments, $companionArguments)
$companionPlan = New-AibosCutoverTaskPlan $script:taskName $root 'Synthetic Companion' -AutoStartCompanion
$originalDirectory = [Environment]::CurrentDirectory
try {
    [Environment]::CurrentDirectory = [IO.Path]::GetTempPath()
    $before = $script:disableCalls
    $result = Close-AibosCutoverPlannedTask $companionPlan
    Check ($result.closed -and $result.changed -and $script:disableCalls -eq $before + 1) 'Saved Companion launch plan changed meaning across working directories.'
}
finally { [Environment]::CurrentDirectory = $originalDirectory }
$manifest = 'A' * 64
$pinnedArguments = [Security.SecurityElement]::Escape((Get-AibosDesktopActionArguments $root -PinnedLaunchManifestSha256 $manifest))
$pinnedXml = $script:originalXml.Replace($arguments, $pinnedArguments)
$script:xml = $pinnedXml
$pinnedPlan = New-AibosCutoverTaskPlan $script:taskName $root -PinnedLaunchManifestSha256 $manifest
$pinnedPlan = $pinnedPlan | ConvertTo-Json -Depth 6 | ConvertFrom-Json
Check ($pinnedPlan.pinnedLaunchManifestSha256 -ceq $manifest) 'Saved plan lost its pinned generation.'
$before = $script:disableCalls
$result = Close-AibosCutoverPlannedTask $pinnedPlan
Check ($result.closed -and $result.changed -and $script:disableCalls -eq $before + 1) 'Pinned task plan could not close its exact definition.'
Assert-AibosCutoverTaskClosed $pinnedPlan | Out-Null
$script:xml = $script:originalXml
Refuses { Close-AibosCutoverPlannedTask $pinnedPlan } 'Pinned plan accepted an ordinary task.'
$script:xml = $pinnedXml
$badPlan = $pinnedPlan.PSObject.Copy(); $badPlan.pinnedLaunchManifestSha256 = 'B' * 64
Refuses { Close-AibosCutoverPlannedTask $badPlan } 'Pinned plan accepted a changed manifest.'
$legacy = $plan.PSObject.Copy(); $legacy.PSObject.Properties.Remove('pinnedLaunchManifestSha256')
$script:xml = $script:originalXml
Check ((Read-AibosCutoverPlannedTask $legacy).Enabled) 'Legacy normal-launch plan stopped working.'

# Only this mock may receive a dispatch. No host task or viewer is started.
$script:startCalls = 0
function Start-ScheduledTask {
    [CmdletBinding()] param($TaskPath, $TaskName)
    if ($TaskPath -cne '\' -or $TaskName -cne $script:taskName) { throw 'Unbound task dispatch.' }
    $script:startCalls++
}
$script:xml = $pinnedXml
Start-AibosPinnedDesktopTask $script:taskName $root -PinnedLaunchManifestSha256 $manifest
Check ($script:startCalls -eq 1) 'Valid pinned request was not dispatched once.'
foreach ($changed in @($script:originalXml, $pinnedXml.Replace($manifest, ('B' * 64)), $pinnedXml.Replace('<Enabled>true</Enabled>', '<Enabled>false</Enabled>'))) {
    $script:xml = $changed
    Refuses { Start-AibosPinnedDesktopTask $script:taskName $root -PinnedLaunchManifestSha256 $manifest } 'Pinned request dispatched a changed or closed task.'
}
Check ($script:startCalls -eq 1) 'Rejected pinned requests reached dispatch.'
[pscustomobject]@{ ok=$true; checks=$script:checks; schedulerWritesMocked=$true; processStops=0; automaticReenables=0; cohortVerified=$false } | ConvertTo-Json

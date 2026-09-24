[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\CutoverDesktopGraph.ps1')
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('aibos-desktop-graph-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $runRoot 'scripts\lib') -Force | Out-Null
$repoRoot = Split-Path -Parent $PSScriptRoot
foreach ($name in Get-AibosCutoverDesktopScripts) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination (Join-Path $runRoot $name)
}
$shortcutPath = Join-Path $runRoot 'Synthetic Graph.lnk'
$taskName = 'Aibos Synthetic Graph'
$manifest = 'A' * 64
$powerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $powerShell
$shortcut.Arguments = Get-AibosDesktopRequestArguments $runRoot $taskName -PinnedLaunchManifestSha256 $manifest
$shortcut.WorkingDirectory = $runRoot; $shortcut.WindowStyle = 7
$shortcut.Description = 'Aibos Image - independent latest local Release launcher'
$shortcut.Save()
$command = [Security.SecurityElement]::Escape($powerShell)
$arguments = [Security.SecurityElement]::Escape((Get-AibosDesktopActionArguments $runRoot -PinnedLaunchManifestSha256 $manifest))
$rootXml = [Security.SecurityElement]::Escape($runRoot)
$owner = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$script:originalXml = @"
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
<RegistrationInfo><Description>Starts Aibos Image independently from the process that requested the launch.</Description></RegistrationInfo><Triggers/>
<Principals><Principal id="Author"><UserId>$owner</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
<Settings><Enabled>true</Enabled><Hidden>false</Hidden></Settings><Actions Context="Author"><Exec><Command>$command</Command><Arguments>$arguments</Arguments><WorkingDirectory>$rootXml</WorkingDirectory></Exec></Actions></Task>
"@
$script:xml = $script:originalXml
$script:reads = 0; $script:blockedWrites = 0; $script:drift = $false; $script:checks = 0
$script:probePath = Join-Path $runRoot 'scripts\start-aibos-fixed-generation.ps1'
function Check([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message }; $script:checks++ }
function Refuses([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    Check $failed $Message
}
function Export-ScheduledTask {
    [CmdletBinding()] param($TaskPath, $TaskName)
    if ($TaskPath -cne '\' -or $TaskName -cne 'Aibos Synthetic Graph') { throw 'Unbound graph fixture read.' }
    $script:reads++
    # The capture must hold real file handles while reading the task/shortcut.
    try {
        $writer = [IO.FileStream]::new($script:probePath, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
        $writer.Dispose()
    }
    catch [IO.IOException] { $script:blockedWrites++ }
    if ($script:drift -and $script:reads -gt 1) { return $script:xml.Replace('<Hidden>false</Hidden>', '<Hidden>true</Hidden>') }
    return $script:xml
}
function Disable-ScheduledTask {
    [CmdletBinding()] param($TaskPath, $TaskName)
    if ($TaskPath -cne '\' -or $TaskName -cne 'Aibos Synthetic Graph') { throw 'Unbound graph fixture close.' }
    $script:xml = $script:xml.Replace('<Enabled>true</Enabled>', '<Enabled>false</Enabled>')
}
function Start-ScheduledTask { throw 'Forbidden real dispatch.' }
function Stop-ScheduledTask { throw 'Forbidden real process stop.' }
function Register-ScheduledTask { throw 'Forbidden task registration.' }
function New-Graph { New-AibosCutoverDesktopGraph $taskName $runRoot $shortcutPath -PinnedLaunchManifestSha256 $manifest }
function CanWriteFixture {
    try { $file = [IO.File]::Open($script:probePath, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite); $file.Dispose(); return $true }
    catch [IO.IOException] { return $false }
}
$plan = New-Graph
Check ($plan.files.Count -eq 10 -and $script:reads -eq 2 -and $script:blockedWrites -eq 2) 'Graph capture omitted dependencies or did not retain actual files.'
Check (CanWriteFixture) 'Successful capture leaked file handles.'
$saved = $plan | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText((Join-Path $runRoot 'graph.json'), $saved, [Text.UTF8Encoding]::new($false))
$plan = $saved | ConvertFrom-Json
$result = Assert-AibosCutoverDesktopGraph $plan
Check ($result.graphVerified -and -not $result.taskClosed -and -not $result.cohortVerified -and -not $result.maintenanceAllowed) 'Graph replay granted authority or failed.'
Close-AibosCutoverPlannedTask $plan.task | Out-Null
$result = Assert-AibosCutoverDesktopGraph $plan -TaskClosed
Check ($result.taskClosed -and -not $result.maintenanceAllowed) 'Closed task did not preserve the same desktop graph.'
Refuses { Assert-AibosCutoverDesktopGraph $plan } 'Closed graph passed its before-phase check.'
$script:xml = $script:originalXml
Refuses { Assert-AibosCutoverDesktopGraph $plan -TaskClosed } 'Reopened task passed the closed-phase check.'
$bad = $saved | ConvertFrom-Json; $bad.task.closedSha256 = $bad.task.beforeSha256
Refuses { Assert-AibosCutoverDesktopGraph $bad -TaskClosed } 'Retargeted closed digest certified an enabled task.'
$bad = $saved | ConvertFrom-Json; $bad.files = @($bad.files | Select-Object -First 9)
Refuses { Assert-AibosCutoverDesktopGraph $bad } 'Saved graph omitted a dependency.'
$bad = $saved | ConvertFrom-Json; $bad.files[1].sha256 = '0' * 64
Refuses { Assert-AibosCutoverDesktopGraph $bad } 'Changed saved digest was accepted.'
$originalBytes = [IO.File]::ReadAllBytes($script:probePath)
try {
    [IO.File]::AppendAllText($script:probePath, "`n# synthetic changed route")
    Refuses { Assert-AibosCutoverDesktopGraph $plan } 'Changed launch script retained graph evidence.'
    Refuses { New-Graph } 'Arbitrary launch script was accepted as the observer package.'
    Check (CanWriteFixture) 'Rejected capture leaked file handles.'
    [IO.File]::WriteAllBytes($script:probePath, [byte[]]::new(1048577))
    Refuses { New-Graph } 'Oversized launch script was read as a valid graph.'
}
finally { [IO.File]::WriteAllBytes($script:probePath, $originalBytes) }
$shortcutBytes = [IO.File]::ReadAllBytes($shortcutPath)
try {
    $shortcut.Arguments = $shortcut.Arguments.Replace($taskName, 'Another Synthetic Task'); $shortcut.Save()
    Refuses { Assert-AibosCutoverDesktopGraph $plan } 'Retargeted shortcut was accepted.'
}
finally { [IO.File]::WriteAllBytes($shortcutPath, $shortcutBytes) }
$script:reads = 0; $script:drift = $true
try { Refuses { New-Graph } 'Task drift during observation was accepted.' }
finally { $script:drift = $false }
$alias = Join-Path $runRoot 'alias'
New-Item -ItemType Junction -Path $alias -Target (Join-Path $runRoot 'scripts') | Out-Null
# A saved shortcut path through a parent junction must not be accepted as the
# explicitly named file, even when it ultimately has exactly the same bytes.
$aliasedShortcut = Join-Path $runRoot 'scripts\Alias Target.lnk'
[IO.File]::WriteAllBytes($aliasedShortcut, $shortcutBytes)
Refuses { New-AibosCutoverDesktopGraph $taskName $runRoot (Join-Path $alias 'Alias Target.lnk') -PinnedLaunchManifestSha256 $manifest } 'Parent alias escaped native handle-path validation.'
Check (CanWriteFixture) 'Failure left fixture artifacts locked.'
[pscustomobject]@{ ok=$true; checks=$script:checks; schedulerWritesMocked=$true; realShortcutAndFileHandles=$true; productionEnrollment=$false; fixtureRoot=$runRoot } | ConvertTo-Json

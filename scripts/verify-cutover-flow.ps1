[CmdletBinding()]
param([Parameter(Mandatory)][string]$WpfDll)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\CutoverDesktopGraph.ps1')
. (Join-Path $PSScriptRoot 'lib\CutoverIntentCommand.ps1')
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('aibos-cutover-flow-' + [guid]::NewGuid().ToString('N'))
$root = Join-Path $runRoot 'enhance'; $control = Join-Path $runRoot 'control'
New-Item -ItemType Directory -Path $root, $control, (Join-Path $runRoot 'scripts\lib') -Force | Out-Null
$repoRoot = Split-Path -Parent $PSScriptRoot
foreach ($name in @((Get-AibosCutoverDesktopScripts)) + @('scripts/invoke-aibos-cutover.ps1', 'scripts/lib/CutoverIntentCommand.ps1')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination (Join-Path $runRoot $name)
}
$utf8 = [Text.UTF8Encoding]::new($false)
$artifactDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($WpfDll))
$names = [string[]]@('PhotoViewer.Wpf.exe','PhotoViewer.Wpf.dll','PhotoViewer.Wpf.deps.json','PhotoViewer.Wpf.runtimeconfig.json','Microsoft.Data.Sqlite.dll','SQLitePCLRaw.batteries_v2.dll','SQLitePCLRaw.core.dll','SQLitePCLRaw.provider.winsqlite3.dll')
[Array]::Sort($names, [StringComparer]::Ordinal)
$manifestText = ($names | ForEach-Object { $_ + '|' + (Get-FileHash -LiteralPath (Join-Path $artifactDirectory $_)).Hash }) -join "`n"
$manifest = (Get-AibosCutoverDigest $manifestText).ToUpperInvariant()
$target = Join-Path $artifactDirectory 'PhotoViewer.Wpf.exe'
$fixtureLaunch = [pscustomobject]@{ status='current'; launchManifestSha256=$manifest; target=$target }
# Only provenance evaluation and OS/scheduler boundaries are replaced. The
# actual built WPF independently pins artifacts, resolves the isolated root,
# computes binding and publishes/reads the production durable intent store.
$freshnessScript = "param([switch]`$Json)`n@'`n" + ($fixtureLaunch | ConvertTo-Json -Compress) + "`n'@`nexit 0"
[IO.File]::WriteAllText((Join-Path $runRoot 'scripts\check-wpf-launch-target.ps1'), $freshnessScript, $utf8)
[IO.File]::AppendAllText((Join-Path $runRoot 'scripts\lib\CutoverBootEvidence.ps1'), @'

function New-AibosCutoverBootAnchor($OperationId, $BindingDigest) {
    $flow.anchorCalls++
    return [pscustomobject]@{
        SchemaVersion=1; Profile='windows-system-reboot-v1'; OperationId=$OperationId; BindingDigest=$BindingDigest
        Windows=[pscustomobject]@{ HostDigest=('b'*64); Computer='SYNTHETIC'; BootTimeUtc='2026-01-01T00:00:00Z'; OsVersion='10.0.99999'; OsBuild='99999' }
        CapturedAtUtc='2026-01-01T00:10:00Z'; RecordId=100; RecordDigest=('c'*64)
    }
}
function Get-AibosCutoverBootEvidence($Anchor, $OperationId, $BindingDigest) {
    $flow.observeCalls++
    if ($Anchor.OperationId -cne $OperationId -or $Anchor.BindingDigest -cne $BindingDigest -or $Anchor.RecordId -ne 100 -or
        $Anchor.CapturedAtUtc -cne '2026-01-01T00:10:00Z') { throw 'Saved original anchor was not used.' }
    return [pscustomobject]@{ OsGenerationEnded=$flow.bootEnded; RequestTimeUtc=$flow.requestUtc }
}
function Assert-AibosCutoverBeforeRequest($Anchor, $OperationId, $BindingDigest) {
    if ($flow.bootEnded -or $Anchor.OperationId -cne $OperationId -or $Anchor.BindingDigest -cne $BindingDigest) { throw 'Fixture no longer precedes the restart request.' }
}
'@, $utf8)
$taskName = 'Aibos Synthetic Flow'; $operation = '11111111-1111-4111-8111-111111111111'
$powerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$shortcutPath = Join-Path $runRoot 'Synthetic Flow.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath); $shortcut.TargetPath = $powerShell
$shortcut.Arguments = Get-AibosDesktopRequestArguments $runRoot $taskName -PinnedLaunchManifestSha256 $manifest
$shortcut.WorkingDirectory = $runRoot; $shortcut.WindowStyle = 7
$shortcut.Description = 'Aibos Image - independent latest local Release launcher'; $shortcut.Save()
$owner = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$command = [Security.SecurityElement]::Escape($powerShell)
$arguments = [Security.SecurityElement]::Escape((Get-AibosDesktopActionArguments $runRoot -PinnedLaunchManifestSha256 $manifest))
$rootXml = [Security.SecurityElement]::Escape($runRoot)
$flow = @{ anchorCalls=0; observeCalls=0; disableCalls=0; bootEnded=$false; requestUtc=[DateTime]::UtcNow.AddMinutes(10).ToString('o'); checks=0; xml=@"
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><RegistrationInfo><Description>Starts Aibos Image independently from the process that requested the launch.</Description></RegistrationInfo><Triggers/>
<Principals><Principal id="Author"><UserId>$owner</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
<Settings><Enabled>true</Enabled></Settings><Actions Context="Author"><Exec><Command>$command</Command><Arguments>$arguments</Arguments><WorkingDirectory>$rootXml</WorkingDirectory></Exec></Actions></Task>
"@ }
function Export-ScheduledTask { [CmdletBinding()] param($TaskPath,$TaskName)
    if ($TaskPath -cne '\' -or $TaskName -cne 'Aibos Synthetic Flow') { throw 'Unbound flow read.' }; return $flow.xml }
function Disable-ScheduledTask { [CmdletBinding()] param($TaskPath,$TaskName)
    if ($TaskPath -cne '\' -or $TaskName -cne 'Aibos Synthetic Flow') { throw 'Unbound flow disable.' }
    $flow.disableCalls++; $flow.xml = $flow.xml.Replace('<Enabled>true</Enabled>', '<Enabled>false</Enabled>') }
function Start-ScheduledTask { throw 'Forbidden flow dispatch.' }
function Stop-ScheduledTask { throw 'Forbidden flow stop.' }
function Check([bool]$Value,[string]$Message) { if (-not $Value) { throw $Message }; $flow.checks++ }
function Refuses([scriptblock]$Action,[string]$Message) { $refused=$false; try { & $Action | Out-Null } catch { $refused=$true }; Check $refused $Message }
$runner = Join-Path $runRoot 'scripts\invoke-aibos-cutover.ps1'
$options = @{ ControlDirectory=$control; OperationId=$operation; ManifestSha256=$manifest }
$savedEnvironment = @{}
$savedInputEncoding = [Console]::InputEncoding
try {
    # Reproduce UTF-8 console input used by CI: raw JSON must not gain a BOM.
    [Console]::InputEncoding = [Text.UTF8Encoding]::new($true)
    foreach ($name in @('FAVORITES','SEEN','SETTINGS','ALBUMS','SEARCH_HISTORY','RECENT','ENHANCEMENT_JOBS')) {
        $key = 'PHOTOVIEWER_WPF_' + $name + '_PATH'
        $savedEnvironment[$key] = [Environment]::GetEnvironmentVariable($key)
        $path = if ($name -eq 'ENHANCEMENT_JOBS') { Join-Path $root 'jobs.json' } else { Join-Path $runRoot ($name + '.json') }
        [Environment]::SetEnvironmentVariable($key, $path)
    }
    $prepared = & $runner -Phase Prepare @options -TaskName $taskName -ShortcutPath $shortcutPath -LifetimeProfile synthetic-local-v1
    Check ($prepared.phase -eq 'Prepare' -and -not $prepared.enrolled -and -not $prepared.maintenanceAllowed -and $flow.anchorCalls -eq 1 -and $flow.disableCalls -eq 0) 'Preparation changed scheduler state or granted authority.'
    $intentPath = Join-Path $control 'intent.json'; $originalHash = (Get-FileHash $intentPath).Hash
    Refuses { & $runner -Phase Prepare @options -TaskName $taskName -ShortcutPath $shortcutPath -LifetimeProfile synthetic-local-v1 } 'Prepare replaced an existing intent.'
    Check ($flow.anchorCalls -eq 1) 'Replay recaptured the OS anchor.'
    $inbox = Join-Path $root 'enqueue-inbox'; New-Item -ItemType Directory -Path $inbox | Out-Null
    $markerPath = Join-Path $inbox 'maintenance.json'; $admissionPath = Join-Path $control 'admission.json'
    [IO.File]::WriteAllText($markerPath, '{"schemaVersion":999,"keep":true}', $utf8)
    $foreignHash = (Get-FileHash $markerPath).Hash
    Refuses { & $runner -Phase Arm @options } 'Arm overwrote an unsupported existing maintenance marker.'
    Check ((Get-FileHash $markerPath).Hash -ceq $foreignHash -and -not (Test-Path $admissionPath)) 'Rejected admission changed existing state.'
    [IO.File]::Move($markerPath, (Join-Path $runRoot 'foreign-marker.saved'))
    $armed = & $runner -Phase Arm @options
    Check ($armed.phase -eq 'Arm' -and $flow.disableCalls -eq 1 -and -not $armed.maintenanceAllowed) 'Arm did not close exactly its saved task.'
    $markerHash = (Get-FileHash $markerPath).Hash; $admissionHash = (Get-FileHash $admissionPath).Hash
    & $runner -Phase Arm @options | Out-Null
    Check ($flow.disableCalls -eq 1 -and (Get-FileHash $intentPath).Hash -ceq $originalHash -and
        (Get-FileHash $markerPath).Hash -ceq $markerHash -and (Get-FileHash $admissionPath).Hash -ceq $admissionHash) 'Arm replay mutated intent/admission or disabled twice.'
    # Simulate the on-disk interruption point after marker publication but
    # before its completion receipt; retain all fixture evidence by moving it.
    [IO.File]::Move($admissionPath, (Join-Path $runRoot 'admission-before-gap.saved'))
    $marker = ConvertFrom-AibosCutoverJson ([IO.File]::ReadAllText($markerPath))
    $marker | Add-Member -NotePropertyName compatibleNote -NotePropertyValue 'retain synthetic additive field'
    [IO.File]::WriteAllText($markerPath, ($marker | ConvertTo-Json -Compress), $utf8)
    $markerHash = (Get-FileHash $markerPath).Hash
    & $runner -Phase Arm @options | Out-Null
    Check ((Get-FileHash $markerPath).Hash -ceq $markerHash -and (Test-Path $admissionPath)) 'Interrupted admission did not resume without rewriting its marker.'
    [IO.File]::Move($markerPath, (Join-Path $runRoot 'lost-marker.saved'))
    Refuses { & $runner -Phase Arm @options } 'Arm reconstructed a gate after recorded completion.'
    Check (-not (Test-Path $markerPath) -and $flow.disableCalls -eq 1) 'Lost admission was silently repaired.'
    [IO.File]::Move((Join-Path $runRoot 'lost-marker.saved'), $markerPath)
    Refuses { & $runner -Phase Observe @options } 'Observe accepted missing OS-generation evidence.'
    $flow.bootEnded = $true
    $observed = & $runner -Phase Observe @options
    Check ($observed.phase -eq 'Observe' -and -not $observed.enrolled -and -not $observed.maintenanceAllowed -and $flow.anchorCalls -eq 1) 'Observation granted authority or replaced its anchor.'
    $futureRequest = $flow.requestUtc; $flow.requestUtc = '2026-01-01T00:59:00Z'
    Refuses { & $runner -Phase Observe @options } 'Admission committed after the request was accepted.'
    $flow.requestUtc = $observed.admission.committedAtUtc
    Refuses { & $runner -Phase Observe @options } 'Equal timestamps proved no strict admission-before-request ordering.'
    $flow.requestUtc = $futureRequest
    $newRoot = Join-Path $runRoot 'different-enhance'; New-Item -ItemType Directory -Path $newRoot | Out-Null
    $env:PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH = Join-Path $newRoot 'jobs.json'
    Refuses { & $runner -Phase Arm @options } 'Resume accepted a different actual Jobs root.'
    $env:PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH = Join-Path $root 'jobs.json'
    $bad = [pscustomobject]@{ action='resume'; operationId=$operation; controlDirectory=$control; configuration=$prepared.intent.configuration; bootAnchor=$prepared.intent.bootAnchor }
    Refuses { Invoke-AibosCutoverIntentCommand $target $manifest $bad } 'Resume accepted a replacement anchor field.'
    $flow.xml = $flow.xml.Replace('<Enabled>false</Enabled>', '<Enabled>true</Enabled>')
    Refuses { & $runner -Phase Arm @options } 'Arm reclosed a task after its OS generation ended.'
    Refuses { & $runner -Phase Observe @options } 'Observe silently reclosed a reopened task.'
    Check ($flow.disableCalls -eq 1 -and (Get-FileHash $intentPath).Hash -ceq $originalHash -and
        (Get-FileHash $markerPath).Hash -ceq $markerHash -and -not (Test-Path (Join-Path $root 'jobs.json'))) 'Rejected flow modified task, intent, marker or Jobs state.'
}
finally {
    [Console]::InputEncoding = $savedInputEncoding
    foreach ($key in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key, $savedEnvironment[$key]) }
}
[pscustomobject]@{ ok=$true; checks=$flow.checks; actualWpfAndIntentStore=$true; schedulerAndOsMocked=$true; enrollment=$false; fixtureRoot=$runRoot } | ConvertTo-Json

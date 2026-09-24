[CmdletBinding()]
param([switch]$PinnedRequestsOnly)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\DesktopActivation.ps1')
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('aibos-launch-arguments-' + [guid]::NewGuid().ToString('N'))
$fixtureScripts = Join-Path $runRoot 'scripts'
New-Item -ItemType Directory -Path $fixtureScripts | Out-Null
# Only this synthetic script is launched; no WPF, Companion or scheduled task.
[IO.File]::WriteAllText((Join-Path $fixtureScripts 'start-aibos-desktop.ps1'), @'
param([string]$CompanionRoot = '', [switch]$AutoStartCompanion, [string]$PinnedLaunchManifestSha256 = '')
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
[pscustomobject]@{ root=$CompanionRoot; auto=[bool]$AutoStartCompanion; manifest=$PinnedLaunchManifestSha256; extra=@($args) } | ConvertTo-Json -Compress
'@, [Text.UTF8Encoding]::new($true))

$checks = 0
$powerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$withSpaces = Join-Path $runRoot 'Companion With Spaces'
$unicodeName = 'Companion ' + [char]0x753b + [char]0x50cf
$unicodePath = Join-Path $runRoot $unicodeName
$candidates = @('', $withSpaces, ($withSpaces + '\'), ($unicodePath + '\'), [IO.Path]::GetPathRoot($runRoot), '\\synthetic-host\synthetic-share\')
if ($PinnedRequestsOnly) { $candidates = @() }
foreach ($candidate in $candidates) {
    foreach ($mode in @('normal', 'auto', 'pinned')) {
        $auto = $mode -eq 'auto'
        $manifest = if ($mode -eq 'pinned') { 'A' * 64 } else { '' }
        $expected = if ($candidate) { [IO.Path]::GetFullPath($candidate) } else { '' }
        $info = [Diagnostics.ProcessStartInfo]::new()
        $info.FileName = $powerShell
        $info.Arguments = Get-AibosDesktopActionArguments $runRoot $candidate -AutoStartCompanion:$auto -PinnedLaunchManifestSha256 $manifest
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        # Make JSON output independent of the host's legacy console encoding.
        $info.StandardOutputEncoding = [Text.Encoding]::UTF8
        $process = $null
        try {
            # Windows PowerShell uses its console output encoding for redirected
            # output too; the child script sets that before serializing paths.
            $process = [Diagnostics.Process]::Start($info)
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(5000)) { throw 'Owned argument probe exceeded five seconds.' }
            if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 2000)) {
                throw 'Owned argument probe output did not close.'
            }
            $reply = $stdout.GetAwaiter().GetResult() | ConvertFrom-Json
            if ($process.ExitCode -ne 0 -or $stderr.GetAwaiter().GetResult().Length -ne 0 -or
                $reply.root -cne $expected -or $reply.auto -ne $auto -or $reply.manifest -cne $manifest -or @($reply.extra).Count -ne 0) {
                throw 'Native PowerShell argument parsing changed the selected Companion or launch option.'
            }
            $checks++
        }
        finally {
            if ($null -ne $process) {
                if (-not $process.HasExited) { $process.Kill(); [void]$process.WaitForExit(2000) }
                $process.Dispose()
            }
        }
    }
}
# Run the actual request script in a synthetic tree. Every scheduler boundary is
# mocked, and an accidental ordinary activation fails before touching an event.
$lib = Join-Path $fixtureScripts 'lib'
New-Item -ItemType Directory -Path $lib | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'request-aibos-desktop.ps1') -Destination $fixtureScripts
foreach ($name in @('DesktopActivation.ps1', 'CutoverManagedTask.ps1', 'CutoverBootEvidence.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('lib\' + $name)) -Destination $lib
}
[IO.File]::AppendAllText((Join-Path $lib 'DesktopActivation.ps1'), "`nfunction Send-AibosDesktopActivation { throw 'Ordinary activation reached by pinned request.' }", [Text.UTF8Encoding]::new($false))
$wrapper = Join-Path $fixtureScripts 'request-fixture.ps1'
[IO.File]::WriteAllText($wrapper, @'
param([string]$Manifest, [string]$Case)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module ScheduledTasks -ErrorAction Stop
function Export-ScheduledTask {
    [CmdletBinding()] param($TaskPath, $TaskName)
    if ($TaskPath -cne '\' -or $TaskName -cne 'Aibos Pinned Request Fixture') { throw 'Unbound fixture read.' }
    return [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'task.xml'))
}
function Start-ScheduledTask {
    [CmdletBinding()] param($TaskPath, $TaskName)
    if ($TaskPath -cne '\' -or $TaskName -cne 'Aibos Pinned Request Fixture') { throw 'Unbound fixture dispatch.' }
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot ($Case + '.dispatched')), 'dispatched')
}
$global:LASTEXITCODE = 0
& (Join-Path $PSScriptRoot 'request-aibos-desktop.ps1') -TaskName 'Aibos Pinned Request Fixture' -PinnedLaunchManifestSha256 $Manifest -CompanionRoot ((Join-Path (Split-Path -Parent $PSScriptRoot) 'Companion With Spaces') + '\')
exit $LASTEXITCODE
'@, [Text.UTF8Encoding]::new($false))
$manifest = 'A' * 64
$owner = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$command = [Security.SecurityElement]::Escape($powerShell)
$action = [Security.SecurityElement]::Escape((Get-AibosDesktopActionArguments $runRoot ($withSpaces + '\') -PinnedLaunchManifestSha256 $manifest))
$working = [Security.SecurityElement]::Escape($runRoot)
$taskXml = @"
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
<RegistrationInfo><Description>Starts Aibos Image independently from the process that requested the launch.</Description></RegistrationInfo>
<Triggers/><Principals><Principal id="Author"><UserId>$owner</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
<Settings><Enabled>true</Enabled></Settings><Actions Context="Author"><Exec><Command>$command</Command><Arguments>$action</Arguments><WorkingDirectory>$working</WorkingDirectory></Exec></Actions></Task>
"@
$requestChecks = 0
foreach ($scenario in @('valid', 'ordinary', 'changed', 'closed')) {
    $xml = switch ($scenario) {
        'ordinary' { $taskXml.Replace((' -PinnedLaunchManifestSha256 ' + $manifest), '') }
        'changed' { $taskXml.Replace($manifest, ('B' * 64)) }
        'closed' { $taskXml.Replace('<Enabled>true</Enabled>', '<Enabled>false</Enabled>') }
        default { $taskXml }
    }
    [IO.File]::WriteAllText((Join-Path $fixtureScripts 'task.xml'), $xml, [Text.UTF8Encoding]::new($false))
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $powerShell
    $info.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -Manifest {1} -Case {2}' -f $wrapper, $manifest, $scenario
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(10000)) { throw 'Pinned request fixture did not settle.' }
        $expectedCode = if ($scenario -eq 'valid') { 0 } else { 2 }
        $dispatched = Test-Path -LiteralPath (Join-Path $fixtureScripts ($scenario + '.dispatched'))
        $errorText = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne $expectedCode -or $dispatched -ne ($scenario -eq 'valid') -or
            $stdout.GetAwaiter().GetResult().Length -ne 0 -or
            ($scenario -eq 'valid' -and $errorText.Length -ne 0) -or
            ($scenario -ne 'valid' -and -not $errorText.StartsWith('Pinned desktop request refused: '))) {
            throw "Unexpected pinned request result for ${scenario}: $errorText"
        }
        $requestChecks++
    }
    finally {
        if (-not $process.HasExited) { $process.Kill(); [void]$process.WaitForExit(2000) }
        $process.Dispose()
    }
}
[pscustomobject]@{ ok=$true; nativeArgumentChecks=$checks; pinnedRequestChecks=$requestChecks; schedulerWrites=0; companionStarts=0; fixtureRoot=$runRoot } | ConvertTo-Json

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\CutoverBootEvidence.ps1')
$script:checks = 0
$operation = '11111111-1111-4111-8111-111111111111'
$binding = 'a' * 64
$kernel = 'Microsoft-Windows-Kernel-General'
$kernelGuid = '{a68ca8b7-004f-d7b6-a698-07e2de0f1f5d}'
$userGuid = '{b0aa8734-56f7-41cc-b2f4-de228e98b946}'

function New-SyntheticEvent($RecordId, $Provider, $ProviderId, $Id, $Time, $Data) {
    $payload = foreach ($entry in $Data.GetEnumerator()) {
        '<Data Name="{0}">{1}</Data>' -f $entry.Key, [Security.SecurityElement]::Escape([string]$entry.Value)
    }
    return '<Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event"><System><Provider Name="{0}" Guid="{1}"/><EventID>{2}</EventID><Version>0</Version><TimeCreated SystemTime="{3}"/><EventRecordID>{4}</EventRecordID><Channel>System</Channel><Computer>SYNTHETIC-HOST</Computer></System><EventData>{5}</EventData></Event>' -f $Provider, $ProviderId, $Id, $Time, $RecordId, ($payload -join '')
}

function New-Fixture {
    $first = New-SyntheticEvent 100 'Synthetic' '{00000000-0000-0000-0000-000000000001}' 1 '2026-01-01T00:09:00Z' @{}
    $request = New-SyntheticEvent 101 'User32' $userGuid 1074 '2026-01-01T00:59:00Z' ([ordered]@{
        param1='synthetic caller'; param2='SYNTHETIC-HOST'; param3='synthetic reason'; param4='0x0'
        param5='locale-dependent text is not parsed'; param6=('Aibos initial cutover ' + $operation + ' ' + $binding); param7='synthetic user'
    })
    $unrelated = (New-SyntheticEvent 102 'EventLog' '{00000000-0000-0000-0000-000000000002}' 6005 '2026-01-01T00:59:01Z' @{}).Replace('<EventData></EventData>', '<EventData><Data>unnamed unrelated data</Data></EventData>')
    $stop = New-SyntheticEvent 103 $kernel $kernelGuid 13 '2026-01-01T00:59:30Z' @{StopTime='2026-01-01T00:59:30Z'}
    $start = New-SyntheticEvent 104 $kernel $kernelGuid 12 '2026-01-01T01:00:01Z' ([ordered]@{
        MajorVersion='10'; MinorVersion='0'; BuildVersion='99999'; QfeVersion='1'; ServiceVersion='0'; BootMode='0'; StartTime='2026-01-01T01:00:00Z'
    })
    $prior = [pscustomobject]@{ HostDigest=('b'*64); Computer='SYNTHETIC-HOST'; BootTimeUtc='2026-01-01T00:00:00Z'; OsVersion='10.0.99999'; OsBuild='99999' }
    $current = [pscustomobject]@{ HostDigest=('b'*64); Computer='SYNTHETIC-HOST'; BootTimeUtc='2026-01-01T01:00:00Z'; OsVersion='10.0.99999'; OsBuild='99999' }
    return [pscustomobject]@{
        Anchor=[pscustomobject]@{ SchemaVersion=1; Profile='windows-system-reboot-v1'; OperationId=$operation; BindingDigest=$binding; Windows=$prior; CapturedAtUtc='2026-01-01T00:10:00Z'; RecordId=100L; RecordDigest=(Get-AibosCutoverDigest $first) }
        Current=$current
        Records=[string[]]@($first, $request, $unrelated, $stop, $start)
    }
}

function Check-Sequence($Fixture) {
    Assert-AibosCutoverBootSequence $Fixture.Anchor $Fixture.Current $Fixture.Records $operation $binding
}
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Assert-Rejected([string]$Name, [scriptblock]$Action, [string]$Reason) {
    $failure = $null
    try { & $Action | Out-Null } catch { $failure = $_.Exception.Message }
    if ($null -eq $failure -or $failure -notmatch $Reason) { throw "Unexpected result for ${Name}: $failure" }
    $script:checks++
}

$f = New-Fixture
$result = Check-Sequence $f
Assert-True ($result.OsGenerationEnded -and -not $result.Enrolled -and -not $result.MaintenanceAllowed) 'Boot evidence incorrectly granted enrollment or repair authority.'
Assert-True ($result.OperationId -ceq $operation -and $result.BindingDigest -ceq $binding -and $result.StartupRecordId -eq 104) 'Boot evidence lost its operation or sequence binding.'

$f = New-Fixture; $f.Current.BootTimeUtc = $f.Anchor.Windows.BootTimeUtc
Assert-Rejected 'request accepted but restart canceled' { Check-Sequence $f } 'subsequent OS boot'
$f = New-Fixture; $f.Records = $f.Records[0..3]
Assert-Rejected 'missing startup event' { Check-Sequence $f } 'single corresponding restart sequence'
foreach ($case in @(@('User32',1073), @('User32',1075), @('Microsoft-Windows-Kernel-Power',41), @('EventLog',6008))) {
    $f = New-Fixture
    $f.Records[2] = New-SyntheticEvent 102 $case[0] $userGuid $case[1] '2026-01-01T00:59:01Z' @{}
    Assert-Rejected 'canceled or failed restart followed by unrelated boot' { Check-Sequence $f } 'canceled, failed, or followed by an unexpected shutdown'
}
$f = New-Fixture; $f.Records[1] = $f.Records[1].Replace($operation, '22222222-2222-4222-8222-222222222222')
Assert-Rejected 'different restart operation' { Check-Sequence $f } 'do not correspond'
$f = New-Fixture; $f.Records[1] = $f.Records[1].Replace('Name="User32"', 'Name="AnotherProvider"')
Assert-Rejected 'same ID from unrelated provider' { Check-Sequence $f } 'single corresponding restart sequence'
$f = New-Fixture; $f.Records[1] = $f.Records[1].Replace($userGuid, $kernelGuid)
Assert-Rejected 'provider identity mismatch' { Check-Sequence $f } 'provider or version'
$f = New-Fixture; $f.Records[3] = $f.Records[3].Replace('<Version>0', '<Version>1')
Assert-Rejected 'future event version' { Check-Sequence $f } 'provider or version'
$f = New-Fixture; $f.Records[1] = $f.Records[1].Replace('</EventData>', '<Data Name="param6">duplicate</Data></EventData>')
Assert-Rejected 'duplicate comment field' { Check-Sequence $f } 'duplicate named event data'
$f = New-Fixture; $f.Records[1] = $f.Records[1].Replace('Name="param6"', 'Name="unknown"')
Assert-Rejected 'unknown event field' { Check-Sequence $f } 'Missing reboot event data'
$f = New-Fixture; $f.Records[1] = $f.Records[1].Replace('</EventData>', '<Data Name="future">extra</Data></EventData>')
Assert-Rejected 'future event layout' { Check-Sequence $f } 'data layout'
$f = New-Fixture; $f.Records[0] += ' '
Assert-Rejected 'anchor replaced at same record ID' { Check-Sequence $f } 'anchor is missing or changed'
$f = New-Fixture; $f.Records = $f.Records[1..4]
Assert-Rejected 'log clear or rollover removed anchor' { Check-Sequence $f } 'anchor is missing or changed'
$f = New-Fixture; $f.Records[2] = $f.Records[1]
Assert-Rejected 'duplicate or regressing record ID' { Check-Sequence $f } 'ordering is inconsistent'
$f = New-Fixture; $f.Records[3] = $f.Records[3].Replace('SYNTHETIC-HOST', 'OTHER-HOST')
Assert-Rejected 'event from another host' { Check-Sequence $f } 'host or ordering'
$f = New-Fixture; $f.Current.HostDigest = 'c'*64
Assert-Rejected 'anchor copied to another host' { Check-Sequence $f } 'OS profile changed'
$f = New-Fixture; $f.Current.OsBuild = '100000'
Assert-Rejected 'OS profile changed' { Check-Sequence $f } 'OS profile changed'
$f = New-Fixture; $f.Anchor.BindingDigest = 'c'*64
Assert-Rejected 'different managed resource binding' { Check-Sequence $f } 'mismatched cutover anchor'
$f = New-Fixture; $f.Anchor.BindingDigest = 'c'*64
Assert-Rejected 'both input bindings retargeted after the original restart' {
    Assert-AibosCutoverBootSequence $f.Anchor $f.Current $f.Records $operation ('c'*64)
} 'do not correspond'
$f = New-Fixture; $f.Records[1] = $f.Records[1].Replace(' ' + $binding, '')
Assert-Rejected 'operation-only comment cannot bind a managed configuration' { Check-Sequence $f } 'do not correspond'
Assert-True ((Get-AibosCutoverRestartComment $operation $binding) -ceq ('Aibos initial cutover ' + $operation + ' ' + $binding)) 'Restart requester comment does not match the observed binding grammar.'
$f = New-Fixture; $f.Anchor.SchemaVersion = 999
Assert-Rejected 'future anchor schema' { Check-Sequence $f } 'mismatched cutover anchor'
$f = New-Fixture; $f.Records[4] = $f.Records[4].Replace('Name="BootMode">0', 'Name="BootMode">1')
Assert-Rejected 'unsupported boot mode' { Check-Sequence $f } 'do not correspond'
$f = New-Fixture; $f.Current.BootTimeUtc = '2026-01-02T01:00:00Z'
Assert-Rejected 'prior sequence reused after another boot' { Check-Sequence $f } 'do not correspond'
$f = New-Fixture; $f.Records += $f.Records[4].Replace('>104<', '>105<')
Assert-Rejected 'multiple boot generations' { Check-Sequence $f } 'single corresponding restart sequence'
$f = New-Fixture; $f.Records[3] = $f.Records[3].Replace('00:59:30', '00:58:30')
Assert-Rejected 'shutdown predates request' { Check-Sequence $f } 'do not correspond'
$f = New-Fixture; $f.Records = [string[]]@($f.Records[0]) * 4097
Assert-Rejected 'too many events' { Check-Sequence $f } 'observation count'
$f = New-Fixture; $f.Records[1] += (' ' * 65536)
Assert-Rejected 'oversized single event' { Check-Sequence $f } '64 KiB bound'
$f = New-Fixture; $f.Records[1] = '<!DOCTYPE Event [<!ENTITY payload "unexpected">]>' + $f.Records[1]
Assert-Rejected 'DTD in event XML' { Check-Sequence $f } 'DTD|DOCTYPE|security'

# Test the production coordinator by replacing only OS read boundaries. No
# result boolean or high-level authorization provider is injected.
$script:osReads = 0
$script:exactReads = 0
$script:changeOsAfterRead = $false
$script:changeAnchorAfterRead = $false
$script:liveFixture = New-Fixture
function Get-AibosCutoverWindowsState {
    $script:osReads++
    $state = $script:liveFixture.Current.PSObject.Copy()
    if ($script:changeOsAfterRead -and $script:osReads -gt 1) {
        $state.BootTimeUtc = '2026-01-02T01:00:00Z'
    }
    return $state
}
function Read-AibosCutoverSystemRecords {
    param([long]$FirstRecordId=0, [switch]$Latest, [switch]$Exact)
    if ($Exact) {
        $script:exactReads++
        if ($script:changeAnchorAfterRead) { return ($script:liveFixture.Records[0] + ' ') }
        return $script:liveFixture.Records[0]
    }
    if ($Latest) { return $script:liveFixture.Records[0] }
    return $script:liveFixture.Records
}
$result = Get-AibosCutoverBootEvidence $script:liveFixture.Anchor $operation $binding
Assert-True ($result.OsGenerationEnded -and $script:osReads -eq 2 -and $script:exactReads -eq 1) 'Production coordinator omitted final live revalidation.'
$script:changeAnchorAfterRead = $true
Assert-Rejected 'log changed during enumeration' { Get-AibosCutoverBootEvidence $script:liveFixture.Anchor $operation $binding } 'anchor changed during observation'
$script:changeAnchorAfterRead = $false; $script:changeOsAfterRead = $true; $script:osReads = 0
Assert-Rejected 'boot changed during enumeration' { Get-AibosCutoverBootEvidence $script:liveFixture.Anchor $operation $binding } 'OS identity changed during observation'
$script:changeOsAfterRead = $false; $script:osReads = 0
$anchor = New-AibosCutoverBootAnchor $operation $binding
Assert-True ($anchor.OperationId -ceq $operation -and $anchor.RecordDigest -ceq $script:liveFixture.Anchor.RecordDigest -and $script:osReads -eq 2) 'Anchor capture lost live identity or record binding.'

$script:liveFixture = New-Fixture
$script:liveFixture.Current = $script:liveFixture.Anchor.Windows.PSObject.Copy()
$script:liveFixture.Records = @($script:liveFixture.Records[0])
$script:osReads = 0; $script:exactReads = 0
Assert-AibosCutoverBeforeRequest $script:liveFixture.Anchor $operation $binding
Assert-True ($script:osReads -eq 2 -and $script:exactReads -eq 1) 'Pre-request guard omitted final observation.'
$script:liveFixture.Anchor.RecordId = 99
Assert-Rejected 'pre-request anchor record identity differs' { Assert-AibosCutoverBeforeRequest $script:liveFixture.Anchor $operation $binding } 'record identity changed'
$script:liveFixture.Anchor.RecordId = 100
$script:liveFixture.Records = (New-Fixture).Records[0..1]
Assert-Rejected 'restart request already present' { Assert-AibosCutoverBeforeRequest $script:liveFixture.Anchor $operation $binding } 'already been observed'
$script:liveFixture = New-Fixture
Assert-Rejected 'later boot cannot re-arm' { Assert-AibosCutoverBeforeRequest $script:liveFixture.Anchor $operation $binding } 'ended or changed'
$script:liveFixture.Current = $script:liveFixture.Anchor.Windows.PSObject.Copy()
$script:liveFixture.Records = @($script:liveFixture.Records[0]); $script:changeAnchorAfterRead = $true
Assert-Rejected 'pre-request log changed' { Assert-AibosCutoverBeforeRequest $script:liveFixture.Anchor $operation $binding } 'anchor changed'
$script:changeAnchorAfterRead = $false; $script:changeOsAfterRead = $true; $script:osReads = 0
Assert-Rejected 'pre-request OS changed while reading' { Assert-AibosCutoverBeforeRequest $script:liveFixture.Anchor $operation $binding } 'OS identity changed'

[pscustomobject]@{
    ok=$true; checks=$script:checks; evidence='synthetic raw Windows XML and mocked local OS reads'
    restartRequests=0; schedulerWrites=0; enrollmentWrites=0; repairCalls=0
} | ConvertTo-Json

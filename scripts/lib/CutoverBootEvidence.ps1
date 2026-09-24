# Read-only Windows evidence for an explicit initial managed cutover.
# This component never requests restart, changes launchers, enrolls a root, or
# authorizes repair. A transaction must also prove managed-entry closure and
# external-work lifetime, and bind this observation to its durable intent.
Set-StrictMode -Version Latest

function Get-AibosCutoverDigest([string]$Text) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash(
            [Text.Encoding]::UTF8.GetBytes($Text)))).Replace('-', '').ToLowerInvariant()
    }
    finally { $sha.Dispose() }
}

function Assert-AibosCutoverIdentity([string]$OperationId, [string]$BindingDigest) {
    if ($OperationId -cnotmatch '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' -or
        $OperationId -eq '00000000-0000-0000-0000-000000000000' -or
        $BindingDigest -cnotmatch '^[0-9a-f]{64}$') {
        throw 'Invalid cutover operation or binding digest.'
    }
}

function Get-AibosCutoverRestartComment([string]$OperationId, [string]$BindingDigest) {
    Assert-AibosCutoverIdentity $OperationId $BindingDigest
    # The actual restart requester must use this exact comment. Bind both the
    # operation and its immutable resource/launch configuration into the OS log;
    # changing both caller-side digest fields cannot retarget an old sequence.
    return 'Aibos initial cutover ' + $OperationId + ' ' + $BindingDigest
}

function ConvertTo-AibosCutoverUtc([string]$Text) {
    if ($Text.Length -gt 40 -or $Text -cnotmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,9})?Z$') {
        throw 'Invalid UTC time in cutover evidence.'
    }
    return [Xml.XmlConvert]::ToDateTimeOffset($Text).UtcDateTime
}

function Read-AibosCutoverEvent([string]$RawXml) {
    if ([Text.Encoding]::UTF8.GetByteCount($RawXml) -gt 65536) {
        throw 'Cutover event exceeds the 64 KiB bound.'
    }
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 65536
    $textReader = [IO.StringReader]::new($RawXml)
    $reader = [Xml.XmlReader]::Create($textReader, $settings)
    try {
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
    }
    finally { $reader.Dispose(); $textReader.Dispose() }
    $ns = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $ns.AddNamespace('e', 'http://schemas.microsoft.com/win/2004/08/events/event')
    if ($document.DocumentElement.LocalName -cne 'Event' -or
        $document.DocumentElement.NamespaceURI -cne $ns.LookupNamespace('e') -or
        $document.SelectNodes('/e:Event/e:System', $ns).Count -ne 1) {
        throw 'Invalid Windows System event envelope.'
    }
    $system = $document.SelectSingleNode('/e:Event/e:System', $ns)
    $values = @{}
    foreach ($name in @('Provider', 'EventID', 'Version', 'EventRecordID', 'Channel', 'Computer', 'TimeCreated')) {
        $nodes = $system.SelectNodes('e:' + $name, $ns)
        if ($nodes.Count -ne 1) { throw 'Missing or duplicate System event field.' }
        $values[$name] = $nodes[0]
    }
    if ($values.Channel.InnerText -cne 'System' -or
        $values.EventRecordID.InnerText -cnotmatch '^[1-9][0-9]{0,18}$' -or
        $values.EventID.InnerText -cnotmatch '^[0-9]{1,5}$' -or
        $values.Version.InnerText -cnotmatch '^[0-9]{1,3}$' -or
        [string]::IsNullOrWhiteSpace($values.Computer.InnerText)) {
        throw 'Invalid System event identity.'
    }
    $data = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    $providerName = $values.Provider.GetAttribute('Name')
    $eventId = [int]$values.EventID.InnerText
    # Other System providers can have unnamed data or UserData. Their payload
    # does not participate in reboot evidence, but their record identity does.
    if (($providerName -ceq 'User32' -and $eventId -eq 1074) -or
        ($providerName -ceq 'Microsoft-Windows-Kernel-General' -and $eventId -in @(12, 13))) {
        foreach ($node in $document.SelectNodes('/e:Event/e:EventData/e:Data', $ns)) {
            $name = $node.GetAttribute('Name')
            if ([string]::IsNullOrEmpty($name) -or $data.ContainsKey($name)) {
                throw 'Missing or duplicate named event data.'
            }
            $data.Add($name, $node.InnerText)
        }
    }
    return [pscustomobject]@{
        RecordId = [long]$values.EventRecordID.InnerText
        Id = $eventId
        Version = [int]$values.Version.InnerText
        Provider = $providerName
        ProviderId = $values.Provider.GetAttribute('Guid')
        Computer = $values.Computer.InnerText
        TimeUtc = ConvertTo-AibosCutoverUtc $values.TimeCreated.GetAttribute('SystemTime')
        Data = $data
        Digest = Get-AibosCutoverDigest $RawXml
    }
}

function Get-AibosCutoverWindowsState {
    # Local CIM only; no process inventory, remote host, or privileged mutation.
    $systems = @(Get-CimInstance -ClassName Win32_OperatingSystem `
        -Property LastBootUpTime, Version, BuildNumber, CSName -OperationTimeoutSec 5 -ErrorAction Stop)
    if ($systems.Count -ne 1) { throw 'Ambiguous local operating system.' }
    $os = $systems[0]
    $machineGuid = [string](Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Cryptography' `
        -Name MachineGuid -ErrorAction Stop)
    $parsedGuid = [guid]::Empty
    if (-not [guid]::TryParse($machineGuid, [ref]$parsedGuid) -or $parsedGuid -eq [guid]::Empty -or
        [string]::IsNullOrWhiteSpace($os.CSName)) { throw 'Local host identity is unavailable.' }
    return [pscustomobject]@{
        HostDigest = Get-AibosCutoverDigest ($parsedGuid.ToString('D') + '|' + $os.CSName.ToUpperInvariant())
        Computer = [string]$os.CSName
        BootTimeUtc = $os.LastBootUpTime.ToUniversalTime().ToString('o')
        OsVersion = [string]$os.Version
        OsBuild = [string]$os.BuildNumber
    }
}

function Read-AibosCutoverSystemRecords {
    param([long]$FirstRecordId = 0, [switch]$Latest, [switch]$Exact)
    if ($FirstRecordId -lt 0 -or ($Latest -and $Exact)) { throw 'Invalid event query.' }
    $xpath = '*'
    if (-not $Latest) {
        if ($FirstRecordId -le 0) { throw 'A positive event anchor is required.' }
        $operator = if ($Exact) { '=' } else { '>=' }
        $xpath = '*[System[EventRecordID {0} {1}]]' -f $operator, $FirstRecordId
    }
    $query = [Diagnostics.Eventing.Reader.EventLogQuery]::new(
        'System', [Diagnostics.Eventing.Reader.PathType]::LogName, $xpath)
    $query.ReverseDirection = [bool]$Latest
    $reader = [Diagnostics.Eventing.Reader.EventLogReader]::new($query)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $records = [Collections.Generic.List[string]]::new()
    $bytes = 0L
    try {
        while ($true) {
            if ($watch.Elapsed.TotalSeconds -ge 10) { throw 'System event observation deadline exceeded.' }
            $record = $reader.ReadEvent([TimeSpan]::FromSeconds(1))
            if ($null -eq $record) { break }
            try { $raw = $record.ToXml() } finally { $record.Dispose() }
            $length = [Text.Encoding]::UTF8.GetByteCount($raw)
            $bytes += $length
            if ($length -gt 65536 -or $bytes -gt 16777216 -or $records.Count -ge 4096) {
                throw 'System event observation bound exceeded.'
            }
            $records.Add($raw)
            if ($Latest) { break }
        }
    }
    finally { $reader.Dispose(); $watch.Stop() }
    return $records.ToArray()
}

function New-AibosCutoverBootAnchor {
    param([Parameter(Mandatory)][string]$OperationId, [Parameter(Mandatory)][string]$BindingDigest)
    Assert-AibosCutoverIdentity $OperationId $BindingDigest
    $before = Get-AibosCutoverWindowsState
    $records = @(Read-AibosCutoverSystemRecords -Latest)
    if ($records.Count -ne 1) { throw 'The System log has no stable anchor.' }
    $event = Read-AibosCutoverEvent $records[0]
    $after = Get-AibosCutoverWindowsState
    foreach ($property in @('HostDigest', 'Computer', 'BootTimeUtc', 'OsVersion', 'OsBuild')) {
        if ($before.$property -cne $after.$property) { throw 'OS identity changed during observation.' }
    }
    if ($event.Computer -ine $after.Computer) { throw 'System event belongs to a different host.' }
    return [pscustomobject]@{
        SchemaVersion = 1
        Profile = 'windows-system-reboot-v1'
        OperationId = $OperationId
        BindingDigest = $BindingDigest
        Windows = $after
        CapturedAtUtc = [DateTime]::UtcNow.ToString('o')
        RecordId = $event.RecordId
        RecordDigest = $event.Digest
    }
}

function Assert-AibosCutoverBootSequence {
    # Pure parser boundary for synthetic raw Windows XML. Production callers
    # must use Get-AibosCutoverBootEvidence, which supplies live local readings.
    param($Anchor, $CurrentWindows, [string[]]$Records,
        [string]$OperationId, [string]$BindingDigest)
    Assert-AibosCutoverIdentity $OperationId $BindingDigest
    if ($Anchor.SchemaVersion -ne 1 -or $Anchor.Profile -cne 'windows-system-reboot-v1' -or
        $Anchor.OperationId -cne $OperationId -or $Anchor.BindingDigest -cne $BindingDigest -or
        $Anchor.RecordDigest -cnotmatch '^[0-9a-f]{64}$' -or $Anchor.RecordId -le 0 -or
        $Anchor.Windows.HostDigest -cnotmatch '^[0-9a-f]{64}$') {
        throw 'Unsupported or mismatched cutover anchor.'
    }
    foreach ($property in @('HostDigest', 'Computer', 'OsVersion', 'OsBuild')) {
        if ($Anchor.Windows.$property -cne $CurrentWindows.$property) {
            throw 'Host or supported OS profile changed.'
        }
    }
    $oldBoot = ConvertTo-AibosCutoverUtc $Anchor.Windows.BootTimeUtc
    $newBoot = ConvertTo-AibosCutoverUtc $CurrentWindows.BootTimeUtc
    $captured = ConvertTo-AibosCutoverUtc $Anchor.CapturedAtUtc
    if ($captured -lt $oldBoot -or $newBoot -le $captured) {
        throw 'A subsequent OS boot has not been observed.'
    }
    if ($Records.Count -lt 1 -or $Records.Count -gt 4096) { throw 'Invalid System event observation count.' }
    $events = [Collections.Generic.List[object]]::new()
    $totalBytes = 0L
    $lastId = 0L
    foreach ($raw in $Records) {
        $totalBytes += [Text.Encoding]::UTF8.GetByteCount($raw)
        if ($totalBytes -gt 16777216) { throw 'System event observation bound exceeded.' }
        $event = Read-AibosCutoverEvent $raw
        if ($event.Computer -ine $CurrentWindows.Computer -or $event.RecordId -le $lastId) {
            throw 'System event host or ordering is inconsistent.'
        }
        $lastId = $event.RecordId
        $events.Add($event)
    }
    if ($events[0].RecordId -ne $Anchor.RecordId -or $events[0].Digest -cne $Anchor.RecordDigest) {
        throw 'System log anchor is missing or changed.'
    }
    $requests = [Collections.Generic.List[object]]::new()
    $shutdowns = [Collections.Generic.List[object]]::new()
    $startups = [Collections.Generic.List[object]]::new()
    foreach ($event in $events) {
        if ($event.RecordId -eq $Anchor.RecordId) { continue }
        if (($event.Provider -ceq 'User32' -and $event.Id -in @(1073, 1075)) -or
            ($event.Provider -ceq 'Microsoft-Windows-Kernel-Power' -and $event.Id -eq 41) -or
            ($event.Provider -ceq 'EventLog' -and $event.Id -eq 6008)) {
            throw 'Restart was canceled, failed, or followed by an unexpected shutdown.'
        }
        $isRequest = $event.Provider -ceq 'User32' -and $event.Id -eq 1074
        $isKernel = $event.Provider -ceq 'Microsoft-Windows-Kernel-General' -and $event.Id -in @(12, 13)
        if (-not ($isRequest -or $isKernel)) { continue }
        $providerId = if ($isRequest) { '{b0aa8734-56f7-41cc-b2f4-de228e98b946}' } else { '{a68ca8b7-004f-d7b6-a698-07e2de0f1f5d}' }
        if ($event.Version -ne 0 -or $event.ProviderId -ine $providerId) {
            throw 'Unsupported reboot event provider or version.'
        }
        $fields = @(if ($isRequest) { @('param1','param2','param3','param4','param5','param6','param7') }
            elseif ($event.Id -eq 13) { @('StopTime') }
            else { @('MajorVersion','MinorVersion','BuildVersion','QfeVersion','ServiceVersion','BootMode','StartTime') })
        if ($event.Data.Count -ne $fields.Count) { throw 'Unsupported reboot event data layout.' }
        foreach ($field in $fields) {
            if (-not $event.Data.ContainsKey($field)) { throw 'Missing reboot event data.' }
        }
        if ($isRequest) { $requests.Add($event) }
        elseif ($event.Id -eq 13) { $shutdowns.Add($event) }
        else { $startups.Add($event) }
    }
    if ($requests.Count -ne 1 -or $shutdowns.Count -ne 1 -or $startups.Count -ne 1) {
        throw 'A single corresponding restart sequence is required.'
    }
    $request = $requests[0]; $shutdown = $shutdowns[0]; $startup = $startups[0]
    $stopTime = ConvertTo-AibosCutoverUtc $shutdown.Data['StopTime']
    $startTime = ConvertTo-AibosCutoverUtc $startup.Data['StartTime']
    # CIM datetime precision is microseconds; FILETIME can include 100 ns ticks.
    $bootTicks = $newBoot.Ticks - ($newBoot.Ticks % 10)
    $startTicks = $startTime.Ticks - ($startTime.Ticks % 10)
    if ($request.Data['param6'] -cne (Get-AibosCutoverRestartComment $OperationId $BindingDigest) -or
        $request.Data['param2'] -ine $CurrentWindows.Computer -or
        $request.RecordId -ge $shutdown.RecordId -or $shutdown.RecordId -ge $startup.RecordId -or
        $request.TimeUtc -lt $captured -or $stopTime -lt $request.TimeUtc -or $startTime -le $stopTime -or
        $startup.Data['BootMode'] -cne '0' -or $bootTicks -ne $startTicks -or
        $startup.Data['BuildVersion'] -cne $CurrentWindows.OsBuild) {
        throw 'Restart request, shutdown and current OS boot do not correspond.'
    }
    return [pscustomobject]@{
        Profile = $Anchor.Profile
        OperationId = $OperationId
        BindingDigest = $BindingDigest
        HostDigest = $CurrentWindows.HostDigest
        PreviousBootTimeUtc = $Anchor.Windows.BootTimeUtc
        BootTimeUtc = $CurrentWindows.BootTimeUtc
        RequestRecordId = $request.RecordId
        RequestTimeUtc = $request.TimeUtc.ToString('o')
        ShutdownRecordId = $shutdown.RecordId
        StartupRecordId = $startup.RecordId
        OsGenerationEnded = $true
        Enrolled = $false
        MaintenanceAllowed = $false
    }
}

function Get-AibosCutoverBootEvidence {
    param([Parameter(Mandatory)]$Anchor, [Parameter(Mandatory)][string]$OperationId,
        [Parameter(Mandatory)][string]$BindingDigest)
    Assert-AibosCutoverIdentity $OperationId $BindingDigest
    $current = Get-AibosCutoverWindowsState
    $records = @(Read-AibosCutoverSystemRecords -FirstRecordId $Anchor.RecordId)
    $result = Assert-AibosCutoverBootSequence $Anchor $current $records $OperationId $BindingDigest
    # Detect log clear/rollover during enumeration, and never reuse a previous
    # boot's completed sequence after another OS/profile change.
    $anchorAgain = @(Read-AibosCutoverSystemRecords -FirstRecordId $Anchor.RecordId -Exact)
    if ($anchorAgain.Count -ne 1 -or (Get-AibosCutoverDigest $anchorAgain[0]) -cne $Anchor.RecordDigest) {
        throw 'System log anchor changed during observation.'
    }
    $after = Get-AibosCutoverWindowsState
    foreach ($property in @('HostDigest', 'Computer', 'BootTimeUtc', 'OsVersion', 'OsBuild')) {
        if ($current.$property -cne $after.$property) { throw 'OS identity changed during observation.' }
    }
    return $result
}

# Arm must not silently close a reopened task after a restart was requested.
# This is an observation guard, not durable admission or permission to reboot.
function Assert-AibosCutoverBeforeRequest {
    param($Anchor, [string]$OperationId, [string]$BindingDigest)
    Assert-AibosCutoverIdentity $OperationId $BindingDigest
    if ($Anchor.SchemaVersion -ne 1 -or $Anchor.Profile -cne 'windows-system-reboot-v1' -or
        $Anchor.OperationId -cne $OperationId -or $Anchor.BindingDigest -cne $BindingDigest) { throw 'Mismatched pre-request anchor.' }
    $before = Get-AibosCutoverWindowsState
    foreach ($property in @('HostDigest','Computer','BootTimeUtc','OsVersion','OsBuild')) {
        if ($before.$property -cne $Anchor.Windows.$property) { throw 'The original pre-request OS generation has ended or changed.' }
    }
    $records = @(Read-AibosCutoverSystemRecords -FirstRecordId $Anchor.RecordId)
    if ($records.Count -lt 1 -or $records.Count -gt 4096 -or (Get-AibosCutoverDigest $records[0]) -cne $Anchor.RecordDigest) { throw 'Pre-request anchor is unavailable.' }
    $last = 0L; $bytes = 0L
    foreach ($raw in $records) {
        $bytes += [Text.Encoding]::UTF8.GetByteCount($raw)
        if ($bytes -gt 16777216) { throw 'Pre-request event bound exceeded.' }
        $event = Read-AibosCutoverEvent $raw
        if ($last -eq 0 -and $event.RecordId -ne $Anchor.RecordId) { throw 'Pre-request anchor record identity changed.' }
        if ($event.Computer -ine $before.Computer -or $event.RecordId -le $last) { throw 'Invalid pre-request event ordering or host.' }
        $last = $event.RecordId
        if ($event.RecordId -eq $Anchor.RecordId) { continue }
        if (($event.Provider -ceq 'User32' -and $event.Id -in @(1073,1074,1075)) -or
            ($event.Provider -ceq 'Microsoft-Windows-Kernel-General' -and $event.Id -in @(12,13)) -or
            ($event.Provider -ceq 'Microsoft-Windows-Kernel-Power' -and $event.Id -eq 41) -or
            ($event.Provider -ceq 'EventLog' -and $event.Id -eq 6008)) { throw 'A restart request or OS transition has already been observed; do not re-arm.' }
    }
    $again = @(Read-AibosCutoverSystemRecords -FirstRecordId $Anchor.RecordId -Exact)
    if ($again.Count -ne 1 -or (Get-AibosCutoverDigest $again[0]) -cne $Anchor.RecordDigest) { throw 'Pre-request anchor changed during observation.' }
    $after = Get-AibosCutoverWindowsState
    foreach ($property in @('HostDigest','Computer','BootTimeUtc','OsVersion','OsBuild')) {
        if ($after.$property -cne $before.$property) { throw 'Pre-request OS identity changed during observation.' }
    }
}

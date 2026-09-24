# The managed cutover transaction may close one explicitly bound desktop task.
# Closure is not wired into normal startup. Closing this task neither stops its
# existing processes nor proves closure of shortcuts/direct launchers/delegates.
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'DesktopActivation.ps1')
. (Join-Path $PSScriptRoot 'CutoverBootEvidence.ps1')

function Assert-AibosCutoverTaskName([string]$TaskName) {
    if ([string]::IsNullOrWhiteSpace($TaskName) -or $TaskName.Length -gt 64 -or
        $TaskName -notmatch '^[\p{L}\p{N} ._-]+$') { throw 'Invalid managed desktop task name.' }
}

function Read-AibosCutoverDesktopTask {
    param([string]$TaskName, [string]$LauncherRoot, [string]$CompanionRoot = '', [switch]$AutoStartCompanion,
        [string]$PinnedLaunchManifestSha256 = '')
    Assert-AibosCutoverTaskName $TaskName
    $root = [IO.Path]::GetFullPath($LauncherRoot).TrimEnd('\')
    $arguments = Get-AibosDesktopActionArguments $root $CompanionRoot -AutoStartCompanion:$AutoStartCompanion -PinnedLaunchManifestSha256 $PinnedLaunchManifestSha256
    $powerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    # Exact, validated root-folder name; no wildcard inventory or remote CIM.
    $xml = [string](Export-ScheduledTask -TaskPath '\' -TaskName $TaskName -ErrorAction Stop)
    if ([Text.Encoding]::UTF8.GetByteCount($xml) -gt 65536) { throw 'Managed task XML exceeds 64 KiB.' }
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 65536
    $textReader = [IO.StringReader]::new($xml)
    $reader = [Xml.XmlReader]::Create($textReader, $settings)
    try {
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.PreserveWhitespace = $false
        $document.Load($reader)
    }
    finally { $reader.Dispose(); $textReader.Dispose() }
    $ns = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $ns.AddNamespace('t', 'http://schemas.microsoft.com/windows/2004/02/mit/task')
    if ($document.DocumentElement.LocalName -cne 'Task' -or
        $document.DocumentElement.NamespaceURI -cne $ns.LookupNamespace('t') -or
        $document.DocumentElement.GetAttribute('version') -cnotin @('1.2', '1.3', '1.4')) {
        throw 'Unsupported managed task schema.'
    }
    $nodes = @{}
    foreach ($path in @('RegistrationInfo/Description', 'Principals', 'Principals/Principal', 'Principals/Principal/UserId',
        'Principals/Principal/LogonType', 'Principals/Principal/RunLevel', 'Actions', 'Actions/Exec',
        'Actions/Exec/Command', 'Actions/Exec/Arguments', 'Actions/Exec/WorkingDirectory', 'Settings', 'Settings/Enabled')) {
        $xpath = '/t:Task/t:' + $path.Replace('/', '/t:')
        $selected = $document.SelectNodes($xpath, $ns)
        if ($selected.Count -ne 1) { throw 'Missing or ambiguous managed task definition.' }
        $nodes[$path] = $selected[0]
    }
    $principal = $nodes['Principals/Principal']
    $owner = $nodes['Principals/Principal/UserId'].InnerText
    $ownerSid = if ($owner -match '^S-1-') {
        [Security.Principal.SecurityIdentifier]::new($owner).Value
    } else {
        [Security.Principal.NTAccount]::new($owner).Translate([Security.Principal.SecurityIdentifier]).Value
    }
    $enabledText = $nodes['Settings/Enabled'].InnerText
    if ($ownerSid -cne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -or
        $nodes['RegistrationInfo/Description'].InnerText -cne 'Starts Aibos Image independently from the process that requested the launch.' -or
        $nodes['Principals'].SelectNodes('*').Count -ne 1 -or
        $principal.SelectNodes('t:GroupId', $ns).Count -ne 0 -or
        $nodes['Principals/Principal/LogonType'].InnerText -cne 'InteractiveToken' -or
        $nodes['Principals/Principal/RunLevel'].InnerText -cne 'LeastPrivilege' -or
        $nodes['Actions'].SelectNodes('*').Count -ne 1 -or
        $nodes['Actions'].GetAttribute('Context') -cne $principal.GetAttribute('id') -or
        $nodes['Actions/Exec/Command'].InnerText -ine $powerShell -or
        $nodes['Actions/Exec/Arguments'].InnerText -cne $arguments -or
        $nodes['Actions/Exec/WorkingDirectory'].InnerText.TrimEnd('\') -ine $root -or
        $document.SelectNodes('/t:Task/t:Triggers/*', $ns).Count -ne 0 -or
        $enabledText -cnotin @('true', 'false')) {
        throw 'The selected task does not match the managed desktop launcher.'
    }
    $normalized = $document.DocumentElement.OuterXml
    $nodes['Settings/Enabled'].InnerText = 'false'
    return [pscustomobject]@{
        Xml = $normalized
        Digest = Get-AibosCutoverDigest $normalized
        ClosedDigest = Get-AibosCutoverDigest $document.DocumentElement.OuterXml
        Enabled = $enabledText -ceq 'true'
        OwnerSid = $ownerSid
    }
}

function New-AibosCutoverTaskPlan {
    param([string]$TaskName, [string]$LauncherRoot, [string]$CompanionRoot = '', [switch]$AutoStartCompanion,
        [string]$PinnedLaunchManifestSha256 = '')
    $snapshot = Read-AibosCutoverDesktopTask $TaskName $LauncherRoot $CompanionRoot -AutoStartCompanion:$AutoStartCompanion -PinnedLaunchManifestSha256 $PinnedLaunchManifestSha256
    return [pscustomobject]@{
        profile = 'aibos.desktop-task-closure/v1'
        kind = 'task'
        identity = '\' + $TaskName
        taskName = $TaskName
        taskPath = '\'
        launcherRoot = [IO.Path]::GetFullPath($LauncherRoot)
        companionRoot = if ([string]::IsNullOrWhiteSpace($CompanionRoot)) { '' } else { [IO.Path]::GetFullPath($CompanionRoot) }
        autoStartCompanion = [bool]$AutoStartCompanion
        pinnedLaunchManifestSha256 = $PinnedLaunchManifestSha256
        ownerSid = $snapshot.OwnerSid
        beforeSha256 = $snapshot.Digest
        closedSha256 = $snapshot.ClosedDigest
        beforeXml = $snapshot.Xml
    }
}

function Read-AibosCutoverPlannedTask($Plan) {
    if ($Plan.profile -cne 'aibos.desktop-task-closure/v1' -or $Plan.kind -cne 'task' -or
        $Plan.taskPath -cne '\' -or $Plan.identity -cne ('\' + $Plan.taskName) -or
        $Plan.ownerSid -cne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -or
        $Plan.autoStartCompanion -isnot [bool] -or
        $Plan.beforeSha256 -cnotmatch '^[0-9a-f]{64}$' -or $Plan.closedSha256 -cnotmatch '^[0-9a-f]{64}$' -or
        (Get-AibosCutoverDigest $Plan.beforeXml) -cne $Plan.beforeSha256) {
        throw 'Unsupported or changed managed task plan.'
    }
    # Older normal-launch plans omitted this additive field. A present field
    # must retain its type and canonical value rather than silently reverting.
    $manifest = ''
    if ($null -ne $Plan.PSObject.Properties['pinnedLaunchManifestSha256']) {
        if ($Plan.pinnedLaunchManifestSha256 -isnot [string]) { throw 'Invalid saved launch manifest.' }
        $manifest = $Plan.pinnedLaunchManifestSha256
    }
    return Read-AibosCutoverDesktopTask $Plan.taskName $Plan.launcherRoot $Plan.companionRoot -AutoStartCompanion:$Plan.autoStartCompanion -PinnedLaunchManifestSha256 $manifest
}

function Start-AibosPinnedDesktopTask {
    param([string]$TaskName, [string]$LauncherRoot, [string]$CompanionRoot = '', [string]$PinnedLaunchManifestSha256)
    if ([string]::IsNullOrEmpty($PinnedLaunchManifestSha256)) { throw 'A pinned desktop request requires a manifest.' }
    $configuration = Enter-AibosDesktopConfiguration
    try {
        $current = Read-AibosCutoverDesktopTask $TaskName $LauncherRoot $CompanionRoot -PinnedLaunchManifestSha256 $PinnedLaunchManifestSha256
        if (-not $current.Enabled) { throw 'The pinned desktop task is closed.' }
        # Keep cooperating installers/closure out of the read-to-dispatch gap.
        # This does not exclude an uncoordinated OS writer or certify child startup.
        Start-ScheduledTask -TaskPath '\' -TaskName $TaskName -ErrorAction Stop
    }
    finally {
        try { $configuration.ReleaseMutex() }
        finally { $configuration.Dispose() }
    }
}

function Close-AibosCutoverPlannedTask($Plan) {
    $configuration = Enter-AibosDesktopConfiguration
    try {
        # Internal Arm step only: the upper transaction must have durably saved
        # this plan first. After requesting OS restart, use only the read-only
        # assertion below; a reopened entry invalidates that operation's continuity.
        $current = Read-AibosCutoverPlannedTask $Plan
        if (-not $current.Enabled -and $current.Digest -ceq $Plan.closedSha256) {
            return [pscustomobject]@{ identity=$Plan.identity; closed=$true; changed=$false; cohortVerified=$false }
        }
        if (-not $current.Enabled -or $current.Digest -cne $Plan.beforeSha256 -or $current.ClosedDigest -cne $Plan.closedSha256) {
            throw 'Managed task changed since preparation.'
        }
        Disable-ScheduledTask -TaskPath '\' -TaskName $Plan.taskName -ErrorAction Stop | Out-Null
        Assert-AibosCutoverTaskClosed $Plan | Out-Null
        return [pscustomobject]@{ identity=$Plan.identity; closed=$true; changed=$true; cohortVerified=$false }
    }
    finally {
        try { $configuration.ReleaseMutex() }
        finally { $configuration.Dispose() }
    }
}

function Assert-AibosCutoverTaskClosed($Plan) {
    $configuration = Enter-AibosDesktopConfiguration
    try {
        $current = Read-AibosCutoverPlannedTask $Plan
        if ($current.Enabled -or $current.Digest -cne $Plan.closedSha256) {
            throw 'Managed task closure is not intact.'
        }
        return [pscustomobject]@{ identity=$Plan.identity; closed=$true; changed=$false; cohortVerified=$false }
    }
    finally {
        try { $configuration.ReleaseMutex() }
        finally { $configuration.Dispose() }
    }
}

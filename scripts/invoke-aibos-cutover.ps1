[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory)][ValidateSet('Prepare', 'Arm', 'Observe')][string]$Phase,
    [Parameter(Mandatory)][string]$ControlDirectory,
    [Parameter(Mandatory)][string]$OperationId,
    [Parameter(Mandatory)][string]$ManifestSha256,
    [string]$TaskName = '', [string]$ShortcutPath = '', [string]$CompanionRoot = '',
    [string]$LifetimeProfile = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\CutoverDesktopGraph.ps1')
. (Join-Path $PSScriptRoot 'lib\CutoverIntentCommand.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
$control = [IO.Path]::GetFullPath($ControlDirectory)
if (-not (Test-Path -LiteralPath $control -PathType Container)) { throw 'Cutover requires an explicitly prepared existing control directory.' }
Assert-AibosCutoverIdentity $OperationId ('0' * 64)
if (-not $PSCmdlet.ShouldProcess($control, ($Phase + ' the explicit managed cutover intent; never request restart or enable repair'))) { return }
$freshness = & (Join-Path $PSScriptRoot 'check-wpf-launch-target.ps1') -Json
if ($LASTEXITCODE -ne 0) { throw 'Cutover requires a current recorded Release build.' }
$launch = ConvertFrom-AibosCutoverJson ($freshness -join "`n")
if ($launch.status -cne 'current' -or $launch.launchManifestSha256 -cne $ManifestSha256) { throw 'Cutover Release differs from the selected generation.' }
$configurationLock = Enter-AibosDesktopConfiguration
$admission = $null
try {
    $intentPath = Join-Path $control 'intent.json'
    if ($Phase -eq 'Prepare') {
        if (Test-Path -LiteralPath $intentPath) { throw 'An intent already exists. Resume it with Arm or Observe; do not recapture its anchor.' }
        $graph = New-AibosCutoverDesktopGraph $TaskName $repoRoot $ShortcutPath -PinnedLaunchManifestSha256 $ManifestSha256 -CompanionRoot $CompanionRoot
        $binding = Invoke-AibosCutoverIntentCommand $launch.target $ManifestSha256 ([pscustomobject]@{ action='bind'; desktopGraph=$graph; lifetimeProfile=$LifetimeProfile }) -WorkingDirectory $repoRoot
        $anchor = New-AibosCutoverBootAnchor $OperationId $binding.bindingDigest
        Assert-AibosCutoverDesktopGraph $graph | Out-Null
        $reply = Invoke-AibosCutoverIntentCommand $launch.target $ManifestSha256 ([pscustomobject]@{
            action='prepare'; operationId=$OperationId; controlDirectory=$control; configuration=$binding.configuration; bootAnchor=$anchor
        }) -WorkingDirectory $repoRoot
        Assert-AibosCutoverDesktopGraph $graph | Out-Null
    }
    else {
        # Bounded read only; the native store independently checks aliases, root
        # identity, operation and canonical binding before any Arm mutation.
        $stream = [IO.FileStream]::new($intentPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            if ($stream.Length -lt 1 -or $stream.Length -gt 131072 -or (Get-AibosCutoverFinalFilePath $stream) -ine $intentPath) { throw 'Invalid saved cutover intent file.' }
            $reader = [IO.StreamReader]::new($stream, [Text.UTF8Encoding]::new($false, $true))
            try { $saved = ConvertFrom-AibosCutoverJson $reader.ReadToEnd() }
            finally { $reader.Dispose() }
        }
        finally { $stream.Dispose() }
        if ($saved.operationId -cne $OperationId) { throw 'This is a different cutover operation.' }
        $reply = Invoke-AibosCutoverIntentCommand $launch.target $ManifestSha256 ([pscustomobject]@{
            action='resume'; operationId=$OperationId; controlDirectory=$control; configuration=$saved.configuration
        }) -WorkingDirectory $repoRoot
        $graph = $reply.intent.configuration.desktopGraph
        if ($Phase -eq 'Arm') {
            Assert-AibosCutoverBeforeRequest $reply.intent.bootAnchor $OperationId $reply.intent.bindingDigest
            $currentTask = Read-AibosCutoverPlannedTask $graph.task
            Assert-AibosCutoverDesktopGraph $graph -TaskClosed:(-not $currentTask.Enabled) | Out-Null
            $admissionReply = Invoke-AibosCutoverIntentCommand $launch.target $ManifestSha256 ([pscustomobject]@{
                action='admit'; operationId=$OperationId; controlDirectory=$control; configuration=$saved.configuration
            }) -WorkingDirectory $repoRoot
            $admission = $admissionReply.admission
            Close-AibosCutoverPlannedTask $graph.task | Out-Null
            Assert-AibosCutoverDesktopGraph $graph -TaskClosed | Out-Null
            Assert-AibosCutoverBeforeRequest $reply.intent.bootAnchor $OperationId $reply.intent.bindingDigest
            Invoke-AibosCutoverIntentCommand $launch.target $ManifestSha256 ([pscustomobject]@{
                action='assert-admission'; operationId=$OperationId; controlDirectory=$control; configuration=$saved.configuration
            }) -WorkingDirectory $repoRoot | Out-Null
        }
        else {
            Assert-AibosCutoverDesktopGraph $graph -TaskClosed | Out-Null
            $admissionReply = Invoke-AibosCutoverIntentCommand $launch.target $ManifestSha256 ([pscustomobject]@{
                action='assert-admission'; operationId=$OperationId; controlDirectory=$control; configuration=$saved.configuration
            }) -WorkingDirectory $repoRoot
            $admission = $admissionReply.admission
            $evidence = Get-AibosCutoverBootEvidence $reply.intent.bootAnchor $OperationId $reply.intent.bindingDigest
            if (-not $evidence.OsGenerationEnded) { throw 'Cutover OS generation has not been observed.' }
            if ((ConvertTo-AibosCutoverUtc $evidence.RequestTimeUtc) -le (ConvertTo-AibosCutoverUtc $admission.committedAtUtc)) {
                throw 'Cutover admission was not committed before the observed restart request.'
            }
            Assert-AibosCutoverDesktopGraph $graph -TaskClosed | Out-Null
            Invoke-AibosCutoverIntentCommand $launch.target $ManifestSha256 ([pscustomobject]@{
                action='assert-admission'; operationId=$OperationId; controlDirectory=$control; configuration=$saved.configuration
            }) -WorkingDirectory $repoRoot | Out-Null
        }
    }
    [pscustomobject]@{ phase=$Phase; operationId=$OperationId; intent=$reply.intent; admission=$admission; enrolled=$false; maintenanceAllowed=$false }
}
finally {
    try { $configurationLock.ReleaseMutex() }
    finally { $configurationLock.Dispose() }
}

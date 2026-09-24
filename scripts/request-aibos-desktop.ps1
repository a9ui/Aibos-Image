[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[\p{L}\p{N} ._-]{1,64}$')]
    [string]$TaskName,
    [string]$PinnedLaunchManifestSha256 = '',
    [string]$CompanionRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
try {
    . (Join-Path $PSScriptRoot 'lib\DesktopActivation.ps1')
    if ($PSBoundParameters.ContainsKey('PinnedLaunchManifestSha256')) {
        . (Join-Path $PSScriptRoot 'lib\CutoverManagedTask.ps1')
        Start-AibosPinnedDesktopTask -TaskName $TaskName -LauncherRoot (Split-Path -Parent $PSScriptRoot) -CompanionRoot $CompanionRoot -PinnedLaunchManifestSha256 $PinnedLaunchManifestSha256
    }
    else {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        if (Send-AibosDesktopActivation -Identity $identity) { exit 0 }
        Start-ScheduledTask -TaskPath '\' -TaskName $TaskName -ErrorAction Stop
    }
}
catch {
    if ($PSBoundParameters.ContainsKey('PinnedLaunchManifestSha256')) {
        [Console]::Error.WriteLine('Pinned desktop request refused: ' + $_.Exception.Message)
        exit 2
    }
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show(
        'Aibos Image could not be opened. Run the desktop launcher installer again, or use start_aibos.bat to see the error.',
        'Aibos Image startup failed') | Out-Null
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}

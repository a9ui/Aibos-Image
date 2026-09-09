[CmdletBinding()]
param(
    [string]$CompanionRoot = '',
    [switch]$AutoStartCompanion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
trap {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show(
        'Aibos Image could not start. Run start_aibos.bat to see the diagnostic output, or reinstall the desktop launcher.',
        'Aibos Image startup failed') | Out-Null
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
. (Join-Path $PSScriptRoot 'lib\DesktopActivation.ps1')
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if (Send-AibosDesktopActivation -Identity $identity) { exit 0 }

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$launcher = Join-Path $repoRoot 'start_aibos.bat'

if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
    throw "Aibos launcher was not found: $launcher"
}

if (-not [string]::IsNullOrWhiteSpace($CompanionRoot)) {
    $resolvedCompanionRoot = [IO.Path]::GetFullPath($CompanionRoot)
    # Preserve the explicit selection even if the optional runtime is offline.
    # WPF validates it when the user requests Enhancement, not during viewing.
    $env:AIBOS_COMPANION_ROOT = $resolvedCompanionRoot
}

# Scheduled launches have no console in which to acknowledge a batch pause.
$env:AIBOS_DESKTOP_LAUNCH = '1'
$env:AIBOS_COMPANION_START_ON_LAUNCH = if ($AutoStartCompanion) { '1' } else { '0' }
$startup = [Threading.Mutex]::new($false, ('Local\AibosImage.Wpf.Startup.v1.' + (Get-AibosDesktopIdentitySuffix -Identity $identity)))
$locked = $false
$child = $null
try {
    try { $locked = $startup.WaitOne([TimeSpan]::FromMinutes(3)) }
    catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { throw 'Another Aibos startup is still preparing. Try again after it finishes.' }
    # Serialize cold-start preparation, not the lifetime of a retained Companion.
    if (Send-AibosDesktopActivation -Identity $identity) { exit 0 }
    $child = Start-Process -FilePath $env:ComSpec -ArgumentList ('/d /s /c ""{0}""' -f $launcher) -WorkingDirectory $repoRoot -WindowStyle Hidden -PassThru
    while (-not $child.HasExited) {
        if (Send-AibosDesktopActivation -Identity $identity) { break }
        Start-Sleep -Milliseconds 200
        $child.Refresh()
    }
}
finally {
    if ($locked) { $startup.ReleaseMutex() }
    $startup.Dispose()
}
# Waiting here must not own startup exclusion: the task may retain descendants.
$child.WaitForExit()
$code = $child.ExitCode
$child.Dispose()
if ($code -ne 0) {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show(
        'Aibos Image could not start or exited with an error. Run start_aibos.bat to see the diagnostic output.',
        'Aibos Image startup failed') | Out-Null
}
exit $code

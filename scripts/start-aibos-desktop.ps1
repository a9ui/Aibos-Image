[CmdletBinding()]
param(
    [string]$CompanionRoot = ''
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
& $launcher
$code = $LASTEXITCODE
if ($code -ne 0) {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show(
        'Aibos Image could not start or exited with an error. Run start_aibos.bat to see the diagnostic output.',
        'Aibos Image startup failed') | Out-Null
}
exit $code

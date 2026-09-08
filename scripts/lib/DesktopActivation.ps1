# Matches SingleInstanceCoordinator's versioned, per-user activation event.
# Opening an existing event never creates a primary or starts Enhancement.
function Get-AibosDesktopIdentitySuffix {
    param([Parameter(Mandatory)][string]$Identity)

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Identity))
        return ([BitConverter]::ToString($hash)).Replace('-', '').Substring(0, 24)
    }
    finally { $sha.Dispose() }

}

function Get-AibosDesktopTaskName {
    param([Parameter(Mandatory)][string]$Identity)
    return 'Aibos Image Desktop Launcher ' + (Get-AibosDesktopIdentitySuffix -Identity $Identity)
}

function Send-AibosDesktopActivation {
    param([Parameter(Mandatory)][string]$Identity)
    $suffix = Get-AibosDesktopIdentitySuffix -Identity $Identity
    $activation = $null
    try {
        $activation = [Threading.EventWaitHandle]::OpenExisting("Local\AibosImage.Wpf.SingleInstance.v1.Activate.$suffix")
        return $activation.Set()
    }
    catch [Threading.WaitHandleCannotBeOpenedException] { return $false }
    finally { if ($null -ne $activation) { $activation.Dispose() } }
}

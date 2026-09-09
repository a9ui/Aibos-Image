[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Uses one uniquely named real scheduled task, never the installed desktop task.
$suffix = [guid]::NewGuid().ToString('N')
$taskName = 'Aibos Synthetic Relaunch ' + $suffix
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('aibos-relaunch-' + $suffix)
$fixtureScripts = Join-Path $runRoot 'scripts'
$lib = Join-Path $fixtureScripts 'lib'
New-Item -ItemType Directory -Path $lib -Force | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
$powerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
foreach ($name in @('start-aibos-desktop.ps1', 'request-aibos-desktop.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $fixtureScripts
}
$helper = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'lib\DesktopActivation.ps1'))
# Override identity hashing only in this isolated copy; event and mutex APIs stay real.
$helper += "`nfunction Get-AibosDesktopIdentitySuffix { param([string]`$Identity) return '$suffix' }`n"
[IO.File]::WriteAllText((Join-Path $lib 'DesktopActivation.ps1'), $helper, $utf8)
$primaryPath = Join-Path $runRoot 'primary.ps1'
[IO.File]::WriteAllText($primaryPath, @'
param([string]$Suffix)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
# A conflicting build fails instead of silently serializing inside the fixture.
$buildLock = [IO.File]::Open((Join-Path $root 'build.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
try { Start-Sleep -Milliseconds 600 } finally { $buildLock.Dispose() }
$countPath = Join-Path $root 'count.txt'
$count = if (Test-Path -LiteralPath $countPath) { [int][IO.File]::ReadAllText($countPath) + 1 } else { 1 }
$created = $false
$event = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::AutoReset, "Local\AibosImage.Wpf.SingleInstance.v1.Activate.$Suffix", [ref]$created)
if (-not $created) { exit 8 }
try {
    if ($count -eq 1) {
        $child = Start-Process powershell -ArgumentList '-NoProfile -Command Start-Sleep -Seconds 180' -WindowStyle Hidden -PassThru
        [IO.File]::WriteAllText((Join-Path $root 'child.txt'), [string]$child.Id)
    }
    [IO.File]::WriteAllText((Join-Path $root "primary-$count.txt"), [string]$PID)
    [IO.File]::WriteAllText($countPath, [string]$count)
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while (-not (Test-Path -LiteralPath (Join-Path $root "stop-$count"))) {
        if ([DateTime]::UtcNow -gt $deadline) { exit 9 }
        Start-Sleep -Milliseconds 100
    }
}
finally { $event.Dispose() }
'@, $utf8)
[IO.File]::WriteAllText((Join-Path $runRoot 'start_aibos.bat'), ('@echo off' + "`r`n" + 'powershell -NoProfile -ExecutionPolicy Bypass -File "{0}" -Suffix "{1}"' -f $primaryPath, $suffix) + "`r`nexit /b %ERRORLEVEL%`r`n", $utf8)
function Wait-Condition([scriptblock]$Check, [string]$Message) {
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    while (-not (& $Check)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw $Message }
        Start-Sleep -Milliseconds 100
    }
}
$registered = $false
$child = $null
try {
    $action = New-ScheduledTaskAction -Execute $powerShell -Argument ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}"' -f (Join-Path $fixtureScripts 'start-aibos-desktop.ps1')) -WorkingDirectory $runRoot
    $principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -MultipleInstances Parallel -ExecutionTimeLimit ([TimeSpan]::Zero)
    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings | Out-Null
    $registered = $true
    for ($cycle = 1; $cycle -le 3; $cycle++) {
        for ($request = 0; $request -lt 3; $request++) {
            & $powerShell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $fixtureScripts 'request-aibos-desktop.ps1') -TaskName $taskName
            if ($LASTEXITCODE -ne 0) { throw 'Synthetic desktop request failed.' }
        }
        $primaryFile = Join-Path $runRoot "primary-$cycle.txt"
        Wait-Condition { Test-Path -LiteralPath $primaryFile } 'Primary did not start from the same task registration.'
        if ($null -eq $child) {
            $child = [Diagnostics.Process]::GetProcessById([int][IO.File]::ReadAllText((Join-Path $runRoot 'child.txt')))
            $null = $child.Handle
        }
        if ($child.HasExited) { throw 'The retained synthetic child was stopped.' }
        Start-Sleep -Seconds 1
        if ([int][IO.File]::ReadAllText((Join-Path $runRoot 'count.txt')) -ne $cycle) { throw 'Cold requests launched duplicate primaries.' }
        $primary = [Diagnostics.Process]::GetProcessById([int][IO.File]::ReadAllText($primaryFile))
        try {
            [IO.File]::WriteAllText((Join-Path $runRoot "stop-$cycle"), '', $utf8)
            Wait-Condition { $primary.HasExited } 'Synthetic primary did not exit.'
        }
        finally { $primary.Dispose() }
    }
    [pscustomobject]@{ ok = $true; sameTaskRelaunches = 2; coldBursts = 3; retainedChildPreserved = -not $child.HasExited; syntheticOnly = $true; fixtureRoot = $runRoot } | ConvertTo-Json
}
finally {
    if ($registered) {
        Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    }
    if ($null -ne $child) {
        if (-not $child.HasExited) { $child.Kill() }
        $child.Dispose()
    }
}

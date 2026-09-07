[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('aibos-launch-errors-' + [guid]::NewGuid().ToString('N'))
$scriptsRoot = Join-Path $runRoot 'scripts'
$libraryRoot = Join-Path $scriptsRoot 'lib'
New-Item -ItemType Directory -Path $libraryRoot -Force | Out-Null
foreach ($name in @('start-aibos-desktop.ps1', 'request-aibos-desktop.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $scriptsRoot
}
$utf8 = [Text.UTF8Encoding]::new($false)
# Never signal the real application or start a real scheduled task.
[IO.File]::WriteAllText((Join-Path $libraryRoot 'DesktopActivation.ps1'), @'
function Send-AibosDesktopActivation { param($Identity) return $false }
function Start-ScheduledTask { [CmdletBinding()] param($TaskPath, $TaskName) throw 'Synthetic scheduling failure.' }
'@, $utf8)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$powerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$launcher = Join-Path $runRoot 'start_aibos.bat'
$results = [Collections.Generic.List[string]]::new()

function Invoke-Case([string]$Name, [string]$ScriptName, [int]$ExpectedExit, [bool]$ExpectDialog, [string]$Extra = '') {
    $scriptPath = Join-Path $scriptsRoot $ScriptName
    $arguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -File "{0}" {1}' -f $scriptPath, $Extra
    $process = Start-Process -FilePath $powerShell -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $sawDialog = $false
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
            $condition = [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
            $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
                [System.Windows.Automation.TreeScope]::Children, $condition)
            if ($null -ne $window -and $window.Current.Name -eq 'Aibos Image startup failed') {
                $sawDialog = $true
                $buttonCondition = [System.Windows.Automation.PropertyCondition]::new(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::Button)
                $button = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
                if ($null -eq $button) { throw "$Name failure dialog had no acknowledgment button." }
                $pattern = $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
                $pattern.Invoke()
            }
            Start-Sleep -Milliseconds 100
            $process.Refresh()
        }
        if (-not $process.HasExited) { throw "$Name timed out; a hidden pause may still be blocking the launcher." }
        $process.WaitForExit()
        if ($process.ExitCode -ne $ExpectedExit) { throw "$Name expected exit $ExpectedExit, got $($process.ExitCode)." }
        if ($sawDialog -ne $ExpectDialog) { throw "$Name failure feedback did not match the expected outcome." }
        $results.Add($Name)
    }
    finally {
        # Only this verifier's own synthetic launcher is eligible for cleanup.
        if (-not $process.HasExited) {
            & taskkill /PID $process.Id /T /F | Out-Null
            $process.WaitForExit(5000) | Out-Null
        }
        $process.Dispose()
    }
}

[IO.File]::WriteAllText($launcher, "@echo off`r`nexit /b 0`r`n", $utf8)
$offlineRoot = Join-Path $runRoot 'offline-companion'
Invoke-Case 'offline-companion-allows-viewing' 'start-aibos-desktop.ps1' 0 $false ('-CompanionRoot "{0}"' -f $offlineRoot)
[IO.File]::WriteAllText($launcher, "@echo off`r`nexit /b 7`r`n", $utf8)
Invoke-Case 'nonzero-child-shows-feedback' 'start-aibos-desktop.ps1' 7 $true
Copy-Item -LiteralPath (Join-Path $repoRoot 'start_aibos.bat') -Destination $launcher -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'start_wpf.bat') -Destination $runRoot
Invoke-Case 'missing-project-does-not-pause' 'start-aibos-desktop.ps1' 1 $true
Move-Item -LiteralPath $launcher -Destination ($launcher + '.saved')
Invoke-Case 'missing-launcher-shows-feedback' 'start-aibos-desktop.ps1' 1 $true
Invoke-Case 'scheduler-failure-shows-feedback' 'request-aibos-desktop.ps1' 1 $true '-TaskName "Aibos Synthetic Error"'
Move-Item -LiteralPath (Join-Path $libraryRoot 'DesktopActivation.ps1') -Destination (Join-Path $libraryRoot 'DesktopActivation.ps1.saved')
Invoke-Case 'missing-helper-shows-feedback' 'request-aibos-desktop.ps1' 1 $true '-TaskName "Aibos Synthetic Error"'
[pscustomobject]@{ ok = $true; checks = @($results); observedNativeDialogs = $true; syntheticOnly = $true; fixtureRoot = $runRoot } | ConvertTo-Json -Depth 4

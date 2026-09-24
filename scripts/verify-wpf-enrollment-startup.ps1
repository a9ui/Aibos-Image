[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WpfDll,
    [string]$DotnetPath = 'dotnet'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $WpfDll -PathType Leaf)) { throw 'Build the WPF source before this check.' }
$WpfDll = [IO.Path]::GetFullPath($WpfDll)

# Both cases fail packet parsing before SID instance acquisition or root resolution.
# They use the actual App.OnStartup Dispatcher path, never a window or user store.
foreach ($scenario in @('delayed-invalid-input', 'input-timeout')) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $DotnetPath
    $info.Arguments = '"{0}" --maintenance-enrollment-handoff' -f $WpfDll
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if ($scenario -eq 'delayed-invalid-input') {
            Start-Sleep -Seconds 2
            $process.StandardInput.Write('{}')
            $process.StandardInput.Close()
        }
        if (-not $process.WaitForExit(15000)) { throw "WPF startup did not settle: $scenario" }
        if ($scenario -eq 'input-timeout' -and $watch.Elapsed.TotalSeconds -lt 9) { throw 'Input timeout did not exercise the packet deadline.' }
        $reply = $stdout.GetAwaiter().GetResult() | ConvertFrom-Json
        if ($process.ExitCode -ne 2 -or $reply.handoff -ne 'rejected' -or $reply.reason -ne 'handoff-unavailable' -or $reply.enrolled -ne $false -or $reply.maintenanceAllowed -ne $false -or $stderr.GetAwaiter().GetResult().Length -ne 0) {
            throw "Invalid WPF startup outcome: $scenario"
        }
    }
    finally {
        if (-not $process.HasExited) {
            & "$env:SystemRoot\System32\taskkill.exe" /PID $process.Id /T /F 2>$null | Out-Null
            [void]$process.WaitForExit(5000)
        }
        $process.Dispose()
    }
}
# Invalid fixed-generation requests also stop before ordinary instance or store startup.
foreach ($arguments in @('--pinned-launch-manifest', ('--pinned-launch-manifest ' + ('0' * 64)), ('--pinned-launch-manifest ' + ('0' * 64) + ' --unexpected'))) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $DotnetPath
    $info.Arguments = '"{0}" {1}' -f $WpfDll, $arguments
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) { throw 'Fixed-generation rejection did not settle.' }
        if ($process.ExitCode -ne 2 -or $stdout.GetAwaiter().GetResult().Length -ne 0 -or
            -not $stderr.GetAwaiter().GetResult().StartsWith('Fixed-generation launch refused: ')) {
            throw 'Invalid fixed-generation WPF startup outcome.'
        }
    }
    finally {
        if (-not $process.HasExited) {
            & "$env:SystemRoot\System32\taskkill.exe" /PID $process.Id /T /F 2>$null | Out-Null
            [void]$process.WaitForExit(5000)
        }
        $process.Dispose()
    }
}
$directory = Split-Path -Parent $WpfDll
$names = [string[]]@('PhotoViewer.Wpf.exe','PhotoViewer.Wpf.dll','PhotoViewer.Wpf.deps.json','PhotoViewer.Wpf.runtimeconfig.json','Microsoft.Data.Sqlite.dll','SQLitePCLRaw.batteries_v2.dll','SQLitePCLRaw.core.dll','SQLitePCLRaw.provider.winsqlite3.dll')
[Array]::Sort($names, [StringComparer]::Ordinal)
$manifestText = ($names | ForEach-Object { $_ + '|' + (Get-FileHash -LiteralPath (Join-Path $directory $_)).Hash }) -join "`n"
$sha = [Security.Cryptography.SHA256]::Create()
try { $manifest = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($manifestText)))).Replace('-', '') }
finally { $sha.Dispose() }
foreach ($scenario in @('missing-manifest', 'cutover-input-timeout')) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $DotnetPath
    $info.Arguments = '"{0}" --maintenance-cutover-intent' -f $WpfDll
    if ($scenario -eq 'cutover-input-timeout') { $info.Arguments += ' ' + $manifest }
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true; $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) { throw 'Cutover input refusal did not settle.' }
        $reply = $stdout.GetAwaiter().GetResult() | ConvertFrom-Json
        if ($scenario -eq 'cutover-input-timeout' -and $watch.Elapsed.TotalSeconds -lt 9) { throw 'Cutover packet timeout was not exercised.' }
        if ($process.ExitCode -ne 2 -or $reply.action -ne 'rejected' -or $reply.enrolled -ne $false -or
            $reply.maintenanceAllowed -ne $false -or $stderr.GetAwaiter().GetResult().Length -ne 0) { throw 'Unexpected cutover startup refusal.' }
    }
    finally {
        if (-not $process.HasExited) { $process.Kill(); [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
}
Write-Output '{"ok":true,"realWpfStartupChecks":7,"userStateAccess":false}'

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EnhancementRoot,
    [ValidateSet('jobs.json', 'jobs.sqlite3')][string]$JobsFileName = 'jobs.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$target = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\bin\Release\net10.0-windows\PhotoViewer.Wpf.exe'
# This inspection never rebuilds, records provenance, activates a window or enrolls a root.
$freshness = & (Join-Path $PSScriptRoot 'check-wpf-launch-target.ps1') -TargetPath $target -Json
if ($LASTEXITCODE -ne 0) {
    [Console]::Error.WriteLine('Enrollment handoff requires a current, recorded Release build. Prepare it through the normal build workflow first.')
    exit 2
}
$freshnessResult = $freshness | ConvertFrom-Json
$manifest = [string]$freshnessResult.launchManifestSha256
if ($freshnessResult.status -ne 'current' -or $manifest -cnotmatch '^[0-9A-F]{64}$') { throw 'Invalid launch artifact evidence.' }
$root = [IO.Path]::GetFullPath($EnhancementRoot)
if ($root.Contains('"')) { throw 'Invalid Enhancement root.' }
$quotedRoot = $root.TrimEnd('\') + '\.'
$localDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet10\dotnet.exe'
$info = [Diagnostics.ProcessStartInfo]::new()
if (Test-Path -LiteralPath $localDotnet -PathType Leaf) {
    $info.FileName = $localDotnet
    $info.Arguments = '"{0}" --maintenance-enrollment-probe "{1}" {2} {3}' -f ([IO.Path]::ChangeExtension($target, '.dll')), $quotedRoot, $JobsFileName, $manifest
}
else {
    $info.FileName = $target
    $info.Arguments = '--maintenance-enrollment-probe "{0}" {1} {2}' -f $quotedRoot, $JobsFileName, $manifest
}
$info.WorkingDirectory = $repoRoot
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
# The dedicated WPF branch never starts Companion, irrespective of inherited options.
if (-not ('AibosEnrollmentPacketReader' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
public static class AibosEnrollmentPacketReader {
    public static async Task<string> Read(Stream stream) {
        byte[] data = new byte[16385];
        int length = 0;
        while (length < data.Length) {
            int count = await stream.ReadAsync(data, length, data.Length - length);
            if (count == 0) return Encoding.UTF8.GetString(data, 0, length);
            length += count;
        }
        throw new IOException("Handoff output exceeded its bound.");
    }
}
'@
}
$process = [Diagnostics.Process]::Start($info)
try {
    $stdout = [AibosEnrollmentPacketReader]::Read($process.StandardOutput.BaseStream)
    $stderr = [AibosEnrollmentPacketReader]::Read($process.StandardError.BaseStream)
    if (-not $process.WaitForExit(45000)) { throw 'Enrollment inspection timed out.' }
    if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 5000)) { throw 'Handoff output did not close.' }
    $raw = $stdout.GetAwaiter().GetResult()
    if ($stderr.GetAwaiter().GetResult().Length -ne 0) { throw 'Handoff emitted an error.' }
    $reply = $raw | ConvertFrom-Json
    if ($reply.protocol -ne 'aibos.maintenance-enrollment-handoff/v1' -or $reply.enrolled -ne $false -or $reply.maintenanceAllowed -ne $false -or $reply.enrollment -ne 'evidence-unavailable') {
        throw 'Unexpected enrollment authority in a passive handoff.'
    }
    $code = $process.ExitCode
    if (-not (($code -eq 10 -and $reply.handoff -eq 'verified') -or ($code -eq 2 -and $reply.handoff -eq 'rejected'))) { throw 'Invalid handoff outcome.' }
    [Console]::Out.WriteLine($raw)
}
finally {
    if (-not $process.HasExited) {
        # Only the process created above and its own bounded inspection child.
        & "$env:SystemRoot\System32\taskkill.exe" /PID $process.Id /T /F 2>$null | Out-Null
        [void]$process.WaitForExit(5000)
    }
    $process.Dispose()
}
exit $code

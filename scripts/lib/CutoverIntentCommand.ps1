Set-StrictMode -Version Latest

function ConvertFrom-AibosCutoverJson([string]$Text) {
    $options = @{}
    if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) { $options.DateKind = 'String' }
    return $Text | ConvertFrom-Json @options
}

function Invoke-AibosCutoverIntentCommand {
    param([string]$TargetPath, [string]$ManifestSha256, $Request, [string]$WorkingDirectory = (Get-Location).ProviderPath)
    $payload = $Request | ConvertTo-Json -Depth 12 -Compress
    if ([Text.Encoding]::UTF8.GetByteCount($payload) -gt 131072 -or $ManifestSha256.Length -ne 64 -or
        $ManifestSha256 -cnotmatch '^[0-9A-F]{64}$') { throw 'Invalid cutover command packet.' }
    if (-not ('AibosCutoverPacketReader' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
public static class AibosCutoverPacketReader {
    public static async Task<string> Read(Stream stream) {
        byte[] bytes = new byte[262145]; int length = 0;
        while (length < bytes.Length) {
            int count = await stream.ReadAsync(bytes, length, bytes.Length - length);
            if (count == 0) return new UTF8Encoding(false, true).GetString(bytes, 0, length);
            length += count;
        }
        throw new IOException("Cutover command output exceeds its bound.");
    }
}
'@
    }
    $target = [IO.Path]::GetFullPath($TargetPath)
    if ($target.Contains('"')) { throw 'Invalid cutover target path.' }
    $dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet10\dotnet.exe'
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.WorkingDirectory = [IO.Path]::GetFullPath($WorkingDirectory)
    if (Test-Path -LiteralPath $dotnet -PathType Leaf) {
        $info.FileName = $dotnet
        $info.Arguments = '"{0}" --maintenance-cutover-intent {1}' -f ([IO.Path]::ChangeExtension($target, '.dll')), $ManifestSha256
    }
    else { $info.FileName = $target; $info.Arguments = '--maintenance-cutover-intent ' + $ManifestSha256 }
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true; $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = [AibosCutoverPacketReader]::Read($process.StandardOutput.BaseStream)
        $stderr = [AibosCutoverPacketReader]::Read($process.StandardError.BaseStream)
        # .NET Framework's ProcessStartInfo has no StandardInputEncoding.
        # Write explicit UTF-8 bytes on both Windows PowerShell and PowerShell 7.
        $inputBytes = [Text.Encoding]::UTF8.GetBytes($payload)
        $write = $process.StandardInput.BaseStream.WriteAsync($inputBytes, 0, $inputBytes.Length)
        if (-not $write.Wait(15000)) { throw 'Cutover input did not settle.' }
        [void]$write.GetAwaiter().GetResult()
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(30000)) { throw 'Cutover command did not settle.' }
        if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 3000)) { throw 'Cutover command output did not close.' }
        if ($process.ExitCode -ne 0 -or $stderr.GetAwaiter().GetResult().Length -ne 0) { throw 'Cutover intent command refused.' }
        $reply = ConvertFrom-AibosCutoverJson $stdout.GetAwaiter().GetResult()
        if ($reply.action -cne $Request.action -or $reply.enrolled -ne $false -or $reply.maintenanceAllowed -ne $false) {
            throw 'Unexpected cutover authority or response.'
        }
        return $reply
    }
    finally {
        if (-not $process.HasExited) { $process.Kill(); [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
}

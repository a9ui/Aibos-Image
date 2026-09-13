# Intentionally no named parameters: all arguments after the executable are
# application arguments, including values that begin with a dash.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($args.Count -lt 1) { throw 'An executable is required.' }

function ConvertTo-NativeArgument([string]$Value) {
    # Windows argv quoting, including embedded quotes and trailing backslashes.
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

$info = [Diagnostics.ProcessStartInfo]::new()
$info.FileName = [string]$args[0]
$info.Arguments = (@($args | Select-Object -Skip 1 | ForEach-Object { ConvertTo-NativeArgument ([string]$_) }) -join ' ')
$info.WorkingDirectory = (Get-Location).ProviderPath
$info.UseShellExecute = $false
# Do not attach dotnet.exe (a console host) to the launcher's console. Merely
# removing START /WAIT would still leave that console's lifetime relevant.
$info.CreateNoWindow = $true
$process = [Diagnostics.Process]::Start($info)
try {
    try { $process.PriorityClass = [Diagnostics.ProcessPriorityClass]::Normal }
    catch [InvalidOperationException] {
        if (-not $process.HasExited) { throw }
    }
    # Preserve immediate startup failure feedback without waiting for the app's
    # lifetime. Disposing this wrapper neither stops the app nor its Companion.
    if ($process.WaitForExit(1000)) { exit $process.ExitCode }
    exit 0
}
finally { $process.Dispose() }

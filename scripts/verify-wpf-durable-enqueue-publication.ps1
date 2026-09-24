param(
    [Parameter(Mandatory = $true)][string]$AssemblyPath,
    [string]$DotnetPath = '',
    [string]$OutputPath = (Join-Path $env:TEMP ('aibos-enqueue-publication-' + [guid]::NewGuid().ToString('N') + '.json'))
)

$ErrorActionPreference = 'Stop'
$assembly = [IO.Path]::GetFullPath($AssemblyPath)
$resultPath = [IO.Path]::GetFullPath($OutputPath)
if ($resultPath.Contains('"')) { throw 'OutputPath cannot contain a double quote.' }
if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) { throw 'Build the WPF application before this verifier.' }
if (-not $DotnetPath) {
    $localHost = Join-Path $env:LOCALAPPDATA 'Microsoft/dotnet10/dotnet.exe'
    $DotnetPath = if (Test-Path -LiteralPath $localHost) { $localHost } else { (Get-Command dotnet -ErrorAction Stop).Source }
}
Remove-Item -LiteralPath $resultPath -ErrorAction SilentlyContinue
$process = Start-Process -FilePath $DotnetPath -ArgumentList @(('"{0}"' -f $assembly), '--durable-enqueue-publication-smoke', ('"{0}"' -f $resultPath)) -WindowStyle Hidden -PassThru
try {
    if (-not $process.WaitForExit(60000)) { throw 'Durable enqueue UI verifier exceeded 60 seconds.' }
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) { throw 'Durable enqueue UI verifier produced no receipt.' }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    $result | ConvertTo-Json -Depth 6
    if ($process.ExitCode -ne 0 -or $result.ok -ne $true) { throw 'Durable enqueue UI verification failed.' }
}
finally {
    if (-not $process.HasExited) { $process.Kill() }
    $process.Dispose()
}

[CmdletBinding()]
param(
    [ValidateRange(1, 65535)]
    [int]$Port = 3000,
    [string]$ExpectedRevision = ""
)

$ErrorActionPreference = "Stop"

$listeners = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop)
if ($listeners.Count -eq 0) {
    throw "No listener found on port $Port."
}

$nonLoopback = @($listeners | Where-Object { $_.LocalAddress -notin @("127.0.0.1", "::1") })
if ($nonLoopback.Count -gt 0) {
    $addresses = ($nonLoopback.LocalAddress | Sort-Object -Unique) -join ", "
    throw "Port $Port is not loopback-only. Found: $addresses"
}

$runtime = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/api/runtime" -Method Get -TimeoutSec 10
$localhostRuntime = Invoke-RestMethod -Uri "http://localhost:$Port/api/runtime" -Method Get -TimeoutSec 10
if ($runtime.product -ne "Aibos Image") {
    throw "Unexpected runtime product identity: $($runtime.product)"
}
if ($localhostRuntime.buildId -ne $runtime.buildId) {
    throw "localhost and 127.0.0.1 reached different Aibos builds."
}
if ($runtime.serverHost -notin @("127.0.0.1", "::1")) {
    throw "Runtime reported a non-loopback host: $($runtime.serverHost)"
}
if ([string]::IsNullOrWhiteSpace([string]$runtime.buildId)) {
    throw "Runtime did not report a build ID."
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedRevision) -and $runtime.sourceRevision -ne $ExpectedRevision) {
    throw "Runtime revision $($runtime.sourceRevision) did not match $ExpectedRevision."
}

$nonLoopbackAddresses = @(
    Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object {
            $_.IPAddress -ne "127.0.0.1" -and
            $_.IPAddress -notlike "169.254.*" -and
            -not $_.SkipAsSource
        } |
        Select-Object -ExpandProperty IPAddress -Unique
)
$unexpectedReachable = @()
foreach ($address in $nonLoopbackAddresses) {
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $connect = $client.ConnectAsync($address, $Port)
        if ($connect.Wait(750) -and $client.Connected) {
            $unexpectedReachable += $address
        }
    }
    catch {
        # Connection refusal is the expected result for a loopback-only server.
    }
    finally {
        $client.Dispose()
    }
}
if ($unexpectedReachable.Count -gt 0) {
    throw "Port $Port was reachable through non-loopback address(es): $($unexpectedReachable -join ', ')"
}

$ownerIds = @($listeners.OwningProcess | Sort-Object -Unique)
$owners = @(foreach ($ownerId in $ownerIds) {
    Get-CimInstance Win32_Process -Filter "ProcessId=$ownerId" |
        Select-Object ProcessId, Name, ExecutablePath, CommandLine
})

[pscustomobject]@{
    ok = $true
    port = $Port
    listenerAddresses = @($listeners.LocalAddress | Sort-Object -Unique)
    nonLoopbackAddressesChecked = $nonLoopbackAddresses
    runtime = $runtime
    owners = $owners
} | ConvertTo-Json -Depth 6

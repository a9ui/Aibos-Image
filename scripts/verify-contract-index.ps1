[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'lib\ContractBundles.ps1')

$result = Test-AibosContractIndex $repoRoot
$result | ConvertTo-Json -Depth 5

[CmdletBinding()]
param(
    [string]$CompanionRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$launcher = Join-Path $repoRoot 'start_aibos.bat'

if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
    throw "Aibos launcher was not found: $launcher"
}

if (-not [string]::IsNullOrWhiteSpace($CompanionRoot)) {
    $resolvedCompanionRoot = [IO.Path]::GetFullPath($CompanionRoot)
    $companionLauncher = Join-Path $resolvedCompanionRoot 'scripts\enhancement_companion.js'
    if (-not (Test-Path -LiteralPath $companionLauncher -PathType Leaf)) {
        throw "The explicitly selected Companion root is invalid."
    }

    $env:AIBOS_COMPANION_ROOT = $resolvedCompanionRoot
}

& $launcher
exit $LASTEXITCODE

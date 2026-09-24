[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-F]{64}$')][string]$ManifestSha256,
    [string]$CompanionRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$target = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\bin\Release\net10.0-windows\PhotoViewer.Wpf.exe'
# Read-only freshness: no batch launcher, dotnet run, rebuild, Record or activation fallback.
$freshness = & (Join-Path $PSScriptRoot 'check-wpf-launch-target.ps1') -TargetPath $target -Json
if ($LASTEXITCODE -ne 0) { throw 'Prepare a current recorded Release build before fixed-generation startup.' }
$observed = $freshness | ConvertFrom-Json
if ($observed.status -cne 'current' -or $observed.launchManifestSha256 -cne $ManifestSha256) {
    throw 'The recorded Release artifact generation no longer matches.'
}
if (-not [string]::IsNullOrWhiteSpace($CompanionRoot)) {
    $env:AIBOS_COMPANION_ROOT = [IO.Path]::GetFullPath($CompanionRoot)
}
$env:AIBOS_COMPANION_START_ON_LAUNCH = '0'
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet10\dotnet.exe'
Push-Location -LiteralPath $repoRoot
try {
    # The WPF startup independently pins and verifies its actual loaded artifact
    # directory before instance activation or stores; a wrapper read is not proof.
    if (Test-Path -LiteralPath $dotnet -PathType Leaf) {
        & (Join-Path $PSScriptRoot 'start-wpf-detached.ps1') $dotnet ([IO.Path]::ChangeExtension($target, '.dll')) '--pinned-launch-manifest' $ManifestSha256
    }
    else {
        & (Join-Path $PSScriptRoot 'start-wpf-detached.ps1') $target '--pinned-launch-manifest' $ManifestSha256
    }
    $code = $LASTEXITCODE
}
finally { Pop-Location }
# Zero is launch dispatch, never enrollment, process settlement or repair authority.
exit $code

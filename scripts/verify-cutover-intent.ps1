[CmdletBinding()]
param([string]$DotnetPath = 'dotnet')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('aibos-cutover-intent-verifier-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
# Persist the actual task adapter's plan through the production intent store.
# Scheduler access remains mocked by the focused adapter verifier.
& (Join-Path $PSScriptRoot 'verify-cutover-managed-task.ps1') -PlanOutputPath (Join-Path $runRoot 'managed-task-plan.json')
foreach ($name in @('MaintenanceCutoverIntentStore', 'WindowsPathIdentity', 'SharedDataRootLocator')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot "local-native\PhotoViewer.Wpf\$name.cs") -Destination $runRoot
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'fixtures\CutoverIntentProgram.cs') -Destination (Join-Path $runRoot 'Program.cs')
[IO.File]::WriteAllText((Join-Path $runRoot 'Fixture.csproj'), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>', [Text.UTF8Encoding]::new($false))
$previousScratch = $env:NUGET_SCRATCH
try {
    $env:NUGET_SCRATCH = Join-Path $runRoot 'nuget-scratch'
    & $DotnetPath build (Join-Path $runRoot 'Fixture.csproj') -c Release --nologo --disable-build-servers -p:UseSharedCompilation=false
}
finally { $env:NUGET_SCRATCH = $previousScratch }
if ($LASTEXITCODE -ne 0) { throw 'Cutover intent fixture build failed.' }
& $DotnetPath (Join-Path $runRoot 'bin\Release\net10.0-windows\Fixture.dll') $runRoot
if ($LASTEXITCODE -ne 0) { throw 'Cutover intent verification failed.' }
& (Join-Path $PSScriptRoot 'verify-cutover-managed-task.ps1') -SavedIntentPath (Join-Path $runRoot 'main\intent.json')
. (Join-Path $PSScriptRoot 'lib\CutoverBootEvidence.ps1')
# Preserve protocol UTC strings on PowerShell versions that auto-convert dates.
$jsonOptions = @{}
if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) { $jsonOptions.DateKind = 'String' }
$saved = Get-Content -LiteralPath (Join-Path $runRoot 'main\intent.json') -Raw -Encoding UTF8 | ConvertFrom-Json @jsonOptions
$events = Get-Content -LiteralPath (Join-Path $runRoot 'observer-events.json') -Raw -Encoding UTF8 | ConvertFrom-Json @jsonOptions
$current = Get-Content -LiteralPath (Join-Path $runRoot 'observer-current.json') -Raw -Encoding UTF8 | ConvertFrom-Json @jsonOptions
if ($saved.restartRequest.comment -cne (Get-AibosCutoverRestartComment $saved.operationId $saved.bindingDigest)) {
    throw 'Saved request differs from the live requester comment grammar.'
}
$evidence = Assert-AibosCutoverBootSequence $saved.bootAnchor $current $events $saved.operationId $saved.bindingDigest
if (-not $evidence.OsGenerationEnded -or $evidence.Enrolled -or $evidence.MaintenanceAllowed) {
    throw 'Saved intent and actual OS predicate did not preserve the authority boundary.'
}
[pscustomobject]@{ savedIntentToOsPredicate=$true; enrolled=$false; maintenanceAllowed=$false; realOsReads=0 } | ConvertTo-Json

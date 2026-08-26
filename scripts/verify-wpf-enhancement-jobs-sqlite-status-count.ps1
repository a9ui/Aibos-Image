param(
    [string]$SourcePath = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SourcePath)) {
    $SourcePath = Join-Path $repoRoot `
        "local-native\PhotoViewer.Wpf\MainWindow.EnhancementJobs.cs"
}
$fullSourcePath = [IO.Path]::GetFullPath($SourcePath)
if (-not (Test-Path -LiteralPath $fullSourcePath -PathType Leaf)) {
    throw "Enhancement Jobs source was not found: $fullSourcePath"
}
$contractPath = Join-Path $repoRoot "contracts\enhancement-jobs-sqlite-v1.json"
if (-not (Test-Path -LiteralPath $contractPath -PathType Leaf)) {
    throw "Enhancement Jobs SQLite contract was not found: $contractPath"
}
$contract = Get-Content -LiteralPath $contractPath -Raw | ConvertFrom-Json
if ($contract.jobsWorkspaceSurface.resourceBounds.maximumRows -ne 100000) {
    throw "The Enhancement Jobs SQLite maximumRows authority is not 100000."
}

$source = Get-Content -LiteralPath $fullSourcePath -Raw
$nextMethodMarker = "private static bool HasRequiredEnhancementWorkspaceProjectionIdentity("
$methodMatch = [regex]::Match(
    $source,
    'private\s+static\s+EnhancementWorkspaceStatusCounts\s+ReadEnhancementWorkspaceStatusCounts\s*\(')
$methodStart = if ($methodMatch.Success) { $methodMatch.Index } else { -1 }
$methodEnd = $source.IndexOf(
    $nextMethodMarker,
    $methodStart,
    [StringComparison]::Ordinal)
if ($methodStart -lt 0 -or $methodEnd -le $methodStart) {
    throw "The bounded SQLite status-count reader could not be located."
}
$method = $source.Substring($methodStart, $methodEnd - $methodStart)

function Assert-Contains([string]$Text, [string]$Expected, [string]$Message) {
    if ($Text.IndexOf($Expected, [StringComparison]::Ordinal) -lt 0) {
        throw $Message
    }
}

Assert-Contains $method "SELECT status" `
    "The status reader must project only status."
Assert-Contains $method "LIMIT `$maximumRowsPlusOne" `
    "The status reader must limit SQLite to maximumRows + 1 rows."
Assert-Contains $method 'checked(EnhancementJobsWorkspaceMaximumRows + 1)' `
    "The SQLite LIMIT must be the checked maximumRows + 1 sentinel."
Assert-Contains $method 'token.ThrowIfCancellationRequested();' `
    "The bounded status reader must remain cancelable."
Assert-Contains $method 'if (total >= EnhancementJobsWorkspaceMaximumRows)' `
    "The maximumRows + 1 sentinel must fail closed in application code."
Assert-Contains $method '未対応の状態が含まれています' `
    "Unknown status values must continue to fail closed."
Assert-Contains $source `
    'private const int EnhancementJobsWorkspaceMaximumRows = 100_000;' `
    "The WPF maximumRows constant must match the SQLite contract."

if ($method -match '(?i)COUNT\s*\(' -or $method -match '(?i)GROUP\s+BY') {
    throw "The status reader must not aggregate or group the untrusted table."
}
if ($method -match '(?i)\b(?:INSERT|UPDATE|DELETE|REPLACE|CREATE|DROP|ALTER)\b') {
    throw "The passive status reader must not contain a SQLite mutation."
}
$boundIndex = $method.IndexOf(
    'if (total >= EnhancementJobsWorkspaceMaximumRows)',
    [StringComparison]::Ordinal)
$statusIndex = $method.IndexOf(
    'string status = ReadRequiredSqliteText',
    [StringComparison]::Ordinal)
if ($boundIndex -lt 0 -or $statusIndex -lt 0 -or $boundIndex -gt $statusIndex) {
    throw "The row-limit sentinel must be checked before reading the extra status value."
}

foreach ($counter in @('queued', 'running', 'succeeded', 'failed', 'canceled', 'deleted')) {
    Assert-Contains $method ($counter + '++;') `
        "The application-side status counter is incomplete: $counter"
}
Assert-Contains $method 'total++;' `
    "The application-side total row counter is missing."

[pscustomobject]@{
    ok = $true
    maximumRows = 100000
    sqliteRowProjectionLimit = 100001
    aggregationRemoved = $true
    unknownStatusFailsClosed = $true
    passiveMutationCount = 0
} | ConvertTo-Json -Depth 3

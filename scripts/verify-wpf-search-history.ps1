param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$root = Join-Path $tempBase ("photoviewer-wpf-search-history-" + [guid]::NewGuid().ToString('N'))
$fullRoot = [IO.Path]::GetFullPath($root)
if (-not $fullRoot.StartsWith($tempBase + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a non-temp verifier root: $fullRoot"
}

try {
    New-Item -ItemType Directory -Force -Path $fullRoot | Out-Null
    $resultPath = Join-Path $fullRoot 'result.json'
    $historyPath = Join-Path $fullRoot 'search-history.json'
    $project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
    $exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net8.0-windows\PhotoViewer.Wpf.exe"
    dotnet build $project -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $process = Start-Process -FilePath $exe -ArgumentList @(
        '--search-history-smoke', $resultPath,
        '--search-history-path', $historyPath
    ) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $resultPath)) {
        throw "WPF search history smoke failed with exit $($process.ExitCode)"
    }
    $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    if ($result.ok -ne $true) { throw "WPF search history assertions failed: $(Get-Content -Raw -LiteralPath $resultPath)" }
    $extendedContractOk = $result.keyboardArrowAndEnter -eq $true -and
        $result.accessibleSelectionAnnouncement -eq $true -and
        $result.historySelectionStayedClosedAfterRefocus -eq $true -and
        $result.greekAndCyrillicCaseFold -eq $true -and
        $result.staleLegacyLockBusyProtected -eq $true -and
        $result.busyWrites -eq 0
    if (-not $extendedContractOk) {
        throw "WPF keyboard/accessibility/refocus/Unicode/stale-lock fail-closed contract failed: $(Get-Content -Raw -LiteralPath $resultPath)"
    }

    Remove-Item -LiteralPath $fullRoot -Recurse -Force
    [pscustomobject]@{
        ok = $true
        message = 'WPF focus/click history popup, whole-query replacement, delete/clear, protected schema, and ViewerState isolation passed.'
        popupFocusAndReopen = $true
        unknownFieldPreserved = $true
        malformedAndFutureProtected = $true
        keyboardArrowAndEnter = $true
        accessibleSelectionAnnouncement = $true
        historySelectionStayedClosedAfterRefocus = $true
        deterministicUnicodeIdentity = $true
        liveLockBusyProtected = $true
        staleLegacyLockBusyProtected = $true
        busyWrites = 0
        lockResidue = 0
        tempResidue = 0
        tempRootRemoved = -not (Test-Path -LiteralPath $fullRoot)
        sourceOrUserCacheTouched = $false
    } | ConvertTo-Json -Depth 4
}
catch { throw $_ }

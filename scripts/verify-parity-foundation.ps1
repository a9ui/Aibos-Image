param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$KeepArtifacts
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempBase + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempBase ('aibos-wpf-contract-v1-' + [guid]::NewGuid().ToString('N'))))
if (-not $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a contract verifier root outside TEMP: $runRoot"
}

$contractPath = Join-Path $repoRoot 'contracts\parity-v1.json'
if (-not (Test-Path -LiteralPath $contractPath -PathType Leaf)) {
    throw "Shared-state contract fixture not found: $contractPath"
}

$wpfReceiptPath = Join-Path $runRoot 'wpf-lf-receipt.json'
$wpfCrlfReceiptPath = Join-Path $runRoot 'wpf-crlf-receipt.json'
$wpfFixtureRoot = Join-Path $runRoot 'wpf-lf-fixtures'
$wpfCrlfFixtureRoot = Join-Path $runRoot 'wpf-crlf-fixtures'
$artifactsRoot = Join-Path $runRoot 'wpf-artifacts'
$crlfContractPath = Join-Path $runRoot 'contract-crlf.json'
$completed = $false

function Quote-ProcessArgument([Parameter(Mandatory = $true)][string]$Value) {
    if ($Value.Contains('"') -or $Value.EndsWith('\')) {
        throw "Unsupported process argument: $Value"
    }
    return '"' + $Value + '"'
}

function Assert-EqualJson([object]$Actual, [object]$Expected, [string]$Label) {
    $actualJson = ConvertTo-Json -InputObject $Actual -Compress -Depth 20
    $expectedJson = ConvertTo-Json -InputObject $Expected -Compress -Depth 20
    if ($actualJson -cne $expectedJson) {
        throw "$Label differed. Actual=$actualJson Expected=$expectedJson"
    }
}

function Get-CanonicalContract([string]$Path) {
    $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
    $text = [IO.File]::ReadAllText($Path, $strictUtf8)
    if ($text.Replace("`r`n", '').Contains("`r")) {
        throw 'Shared-state contract fixture contains a bare carriage return.'
    }
    $canonicalText = $text.Replace("`r`n", "`n")
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($canonicalText)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        [pscustomobject]@{
            Text = $canonicalText
            Sha256 = ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
        }
    }
    finally {
        $sha.Dispose()
    }
}

function Invoke-WpfContract([string]$Executable, [string]$Receipt, [string]$Contract, [string]$FixtureRoot) {
    $arguments = @(
        '--parity-contract-smoke', (Quote-ProcessArgument $Receipt),
        '--contract', (Quote-ProcessArgument $Contract),
        '--temp-root', (Quote-ProcessArgument $FixtureRoot)
    )
    $process = Start-Process -FilePath $Executable -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    try {
        if ($process.ExitCode -ne 0) {
            $receiptText = if (Test-Path -LiteralPath $Receipt) { Get-Content -Raw -LiteralPath $Receipt } else { '<missing>' }
            throw "WPF shared-state vectors failed with exit $($process.ExitCode): $receiptText"
        }
    }
    finally {
        $process.Dispose()
    }
    if (-not (Test-Path -LiteralPath $Receipt -PathType Leaf)) {
        throw 'WPF contract runner did not publish its receipt.'
    }
}

try {
    New-Item -ItemType Directory -Force -Path $runRoot, $wpfFixtureRoot, $wpfCrlfFixtureRoot, $artifactsRoot | Out-Null
    $canonical = Get-CanonicalContract $contractPath
    $contract = $canonical.Text | ConvertFrom-Json
    $expectedContractIds = @($contract.contracts | ForEach-Object { [string]$_.id })
    $expectedCaseIds = @($contract.contracts | ForEach-Object {
        $contractId = [string]$_.id
        @($_.cases) | ForEach-Object { "$contractId/$([string]$_.id)" }
    })

    Push-Location $repoRoot
    try {
        $project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
        $buildOutput = & dotnet build $project -c $Configuration --artifacts-path $artifactsRoot --nologo 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "WPF contract build failed: $($buildOutput -join [Environment]::NewLine)"
        }
    }
    finally {
        Pop-Location
    }

    $wpfExecutable = Join-Path $artifactsRoot ("bin\PhotoViewer.Wpf\{0}\PhotoViewer.Wpf.exe" -f $Configuration.ToLowerInvariant())
    if (-not (Test-Path -LiteralPath $wpfExecutable -PathType Leaf)) {
        throw "WPF contract executable not found: $wpfExecutable"
    }

    Invoke-WpfContract $wpfExecutable $wpfReceiptPath $contractPath $wpfFixtureRoot
    $crlfText = $canonical.Text.Replace("`n", "`r`n")
    [IO.File]::WriteAllText($crlfContractPath, $crlfText, [Text.UTF8Encoding]::new($false))
    Invoke-WpfContract $wpfExecutable $wpfCrlfReceiptPath $crlfContractPath $wpfCrlfFixtureRoot

    $lf = Get-Content -Raw -Encoding utf8 -LiteralPath $wpfReceiptPath | ConvertFrom-Json
    $crlf = Get-Content -Raw -Encoding utf8 -LiteralPath $wpfCrlfReceiptPath | ConvertFrom-Json
    if ($lf.runtime -cne 'wpf' -or $crlf.runtime -cne 'wpf') { throw 'Unexpected contract receipt runtime.' }
    if ($lf.schemaVersion -ne 1 -or $crlf.schemaVersion -ne 1) { throw 'Unsupported contract receipt schema version.' }
    if ($lf.contractSha256 -cne $canonical.Sha256 -or $crlf.contractSha256 -cne $canonical.Sha256) {
        throw "Contract hash mismatch: expected=$($canonical.Sha256) LF=$($lf.contractSha256) CRLF=$($crlf.contractSha256)"
    }
    Assert-EqualJson @($lf.contractIds) $expectedContractIds 'LF contract IDs'
    Assert-EqualJson @($crlf.contractIds) $expectedContractIds 'CRLF contract IDs'
    Assert-EqualJson @($lf.caseIds) $expectedCaseIds 'LF case IDs'
    Assert-EqualJson @($crlf.caseIds) $expectedCaseIds 'CRLF case IDs'
    if ($lf.casesRun -ne $expectedCaseIds.Count -or $crlf.casesRun -ne $expectedCaseIds.Count) {
        throw "Contract case count mismatch: expected=$($expectedCaseIds.Count) LF=$($lf.casesRun) CRLF=$($crlf.casesRun)"
    }
    if (@($lf.failures).Count -ne 0 -or @($crlf.failures).Count -ne 0) {
        throw "WPF contract failures remained: LF=$(@($lf.failures) -join '; ') CRLF=$(@($crlf.failures) -join '; ')"
    }

    $completed = $true
    [pscustomobject]@{
        ok = $true
        runtime = 'wpf'
        schemaVersion = 1
        contractSha256 = $canonical.Sha256
        contractIds = $expectedContractIds
        caseIds = $expectedCaseIds
        cases = $expectedCaseIds.Count
        crlfContractHashVerified = $true
        browserCompatibilityNotRun = $true
        sourceOrUserStateTouched = $false
        artifactsRetained = [bool]$KeepArtifacts
    } | ConvertTo-Json -Depth 8
}
finally {
    if (-not $KeepArtifacts -and (Test-Path -LiteralPath $runRoot)) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        if (-not $resolvedRunRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a contract verifier path outside TEMP: $resolvedRunRoot"
        }
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
    elseif ($KeepArtifacts) {
        Write-Verbose "Contract artifacts retained at $runRoot"
    }
    if (-not $completed -and $KeepArtifacts) {
        Write-Warning "Contract verification failed; artifacts remain at $runRoot"
    }
}

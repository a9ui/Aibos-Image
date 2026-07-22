param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$KeepArtifacts
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempBase + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempBase ('photoviewer-parity-v1-' + [guid]::NewGuid().ToString('N'))))
if (-not $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a parity verifier root outside TEMP: $runRoot"
}

$fullContractPath = Join-Path $repoRoot 'contracts\parity-v1.json'
if (-not (Test-Path -LiteralPath $fullContractPath -PathType Leaf)) {
    throw "Parity contract file not found: $fullContractPath"
}

$previousBrowserReceipt = [Environment]::GetEnvironmentVariable('PVU_PARITY_BROWSER_RECEIPT_PATH', 'Process')
$browserReceiptPath = Join-Path $runRoot 'browser-receipt.json'
$wpfReceiptPath = Join-Path $runRoot 'wpf-receipt.json'
$wpfCrlfReceiptPath = Join-Path $runRoot 'wpf-crlf-receipt.json'
$wpfFixtureRoot = Join-Path $runRoot 'wpf-fixtures'
$wpfCrlfFixtureRoot = Join-Path $runRoot 'wpf-crlf-fixtures'
$artifactsRoot = Join-Path $runRoot 'wpf-artifacts'
$crlfContractPath = Join-Path $runRoot 'parity-v1-crlf.json'
$completed = $false

function Assert-EqualJson {
    param(
        [object]$Actual,
        [object]$Expected,
        [string]$Label
    )
    $actualJson = ConvertTo-Json -InputObject $Actual -Compress -Depth 20
    $expectedJson = ConvertTo-Json -InputObject $Expected -Compress -Depth 20
    if ($actualJson -cne $expectedJson) {
        throw "$Label differed. Actual=$actualJson Expected=$expectedJson"
    }
}

function Quote-ProcessArgument {
    param([Parameter(Mandatory = $true)][string]$Value)
    if ($Value.Contains('"') -or $Value.EndsWith('\')) {
        throw "Unsupported process argument: $Value"
    }
    return '"' + $Value + '"'
}

function Assert-JsonInteger {
    param(
        [object]$Value,
        [string]$Label
    )
    if ($Value -isnot [int] -and $Value -isnot [long]) {
        $typeName = if ($null -eq $Value) { '<null>' } else { $Value.GetType().FullName }
        throw "$Label must be a JSON integer, got $typeName"
    }
}

try {
    New-Item -ItemType Directory -Force -Path $runRoot, $wpfFixtureRoot, $wpfCrlfFixtureRoot, $artifactsRoot | Out-Null

    Push-Location $repoRoot
    try {
        $structureOutput = & node .\scripts\verify-parity-contracts.mjs 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "Parity registry structure verification failed: $($structureOutput -join [Environment]::NewLine)"
        }
        $structure = ($structureOutput -join [Environment]::NewLine) | ConvertFrom-Json

        [Environment]::SetEnvironmentVariable('PVU_PARITY_BROWSER_RECEIPT_PATH', $browserReceiptPath, 'Process')
        $browserOutput = & corepack pnpm exec vitest run src/lib/parityContracts.test.ts --reporter=dot 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "Browser parity vectors failed: $($browserOutput -join [Environment]::NewLine)"
        }
        if (-not (Test-Path -LiteralPath $browserReceiptPath -PathType Leaf)) {
            throw 'Browser parity test did not publish its receipt.'
        }

        $project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
        $buildOutput = & dotnet build $project -c $Configuration --artifacts-path $artifactsRoot --nologo 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "WPF parity build failed: $($buildOutput -join [Environment]::NewLine)"
        }
        $wpfExecutable = Join-Path $artifactsRoot ("bin\PhotoViewer.Wpf\{0}\PhotoViewer.Wpf.exe" -f $Configuration.ToLowerInvariant())
        if (-not (Test-Path -LiteralPath $wpfExecutable -PathType Leaf)) {
            throw "WPF parity executable not found: $wpfExecutable"
        }

        $wpfArguments = @(
            '--parity-contract-smoke', (Quote-ProcessArgument $wpfReceiptPath),
            '--contract', (Quote-ProcessArgument $fullContractPath),
            '--temp-root', (Quote-ProcessArgument $wpfFixtureRoot)
        )
        $wpf = Start-Process -FilePath $wpfExecutable -ArgumentList $wpfArguments -WindowStyle Hidden -Wait -PassThru
        if ($wpf.ExitCode -ne 0) {
            $receiptText = if (Test-Path -LiteralPath $wpfReceiptPath) { Get-Content -Raw -LiteralPath $wpfReceiptPath } else { '<missing>' }
            throw "WPF parity vectors failed with exit $($wpf.ExitCode): $receiptText"
        }
        if (-not (Test-Path -LiteralPath $wpfReceiptPath -PathType Leaf)) {
            throw 'WPF parity runner did not publish its receipt.'
        }

        $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
        $contractText = [IO.File]::ReadAllText($fullContractPath, $strictUtf8)
        if ($contractText.Replace("`r`n", '').Contains("`r")) {
            throw 'Canonical parity contract contains a bare carriage return.'
        }
        $crlfContractText = $contractText.Replace("`r`n", "`n").Replace("`n", "`r`n")
        [IO.File]::WriteAllText($crlfContractPath, $crlfContractText, [Text.UTF8Encoding]::new($false))
        $wpfCrlfArguments = @(
            '--parity-contract-smoke', (Quote-ProcessArgument $wpfCrlfReceiptPath),
            '--contract', (Quote-ProcessArgument $crlfContractPath),
            '--temp-root', (Quote-ProcessArgument $wpfCrlfFixtureRoot)
        )
        $wpfCrlf = Start-Process -FilePath $wpfExecutable -ArgumentList $wpfCrlfArguments -WindowStyle Hidden -Wait -PassThru
        if ($wpfCrlf.ExitCode -ne 0) {
            $receiptText = if (Test-Path -LiteralPath $wpfCrlfReceiptPath) { Get-Content -Raw -LiteralPath $wpfCrlfReceiptPath } else { '<missing>' }
            throw "WPF CRLF parity vectors failed with exit $($wpfCrlf.ExitCode): $receiptText"
        }
        if (-not (Test-Path -LiteralPath $wpfCrlfReceiptPath -PathType Leaf)) {
            throw 'WPF CRLF parity runner did not publish its receipt.'
        }

        $browser = Get-Content -Raw -Encoding utf8 -LiteralPath $browserReceiptPath | ConvertFrom-Json
        $wpfReceipt = Get-Content -Raw -Encoding utf8 -LiteralPath $wpfReceiptPath | ConvertFrom-Json
        $wpfCrlfReceipt = Get-Content -Raw -Encoding utf8 -LiteralPath $wpfCrlfReceiptPath | ConvertFrom-Json
        if ($browser.runtime -cne 'browser') { throw "Unexpected Browser receipt runtime: $($browser.runtime)" }
        if ($wpfReceipt.runtime -cne 'wpf') { throw "Unexpected WPF receipt runtime: $($wpfReceipt.runtime)" }
        if ($wpfCrlfReceipt.runtime -cne 'wpf') { throw "Unexpected WPF CRLF receipt runtime: $($wpfCrlfReceipt.runtime)" }
        Assert-JsonInteger -Value $structure.schemaVersion -Label 'Registry schemaVersion'
        Assert-JsonInteger -Value $browser.schemaVersion -Label 'Browser schemaVersion'
        Assert-JsonInteger -Value $wpfReceipt.schemaVersion -Label 'WPF schemaVersion'
        Assert-JsonInteger -Value $wpfCrlfReceipt.schemaVersion -Label 'WPF CRLF schemaVersion'
        Assert-JsonInteger -Value $structure.cases -Label 'Registry case count'
        Assert-JsonInteger -Value $browser.casesRun -Label 'Browser casesRun'
        Assert-JsonInteger -Value $wpfReceipt.casesRun -Label 'WPF casesRun'
        Assert-JsonInteger -Value $wpfCrlfReceipt.casesRun -Label 'WPF CRLF casesRun'
        if ($browser.schemaVersion -ne 1 -or $wpfReceipt.schemaVersion -ne 1 -or $wpfCrlfReceipt.schemaVersion -ne 1) { throw 'A parity runner used an unsupported schema version.' }
        if ($browser.contractSha256 -cne $structure.sha256 -or $wpfReceipt.contractSha256 -cne $structure.sha256 -or $wpfCrlfReceipt.contractSha256 -cne $structure.sha256) {
            throw "Parity contract hash mismatch: structure=$($structure.sha256) browser=$($browser.contractSha256) wpf=$($wpfReceipt.contractSha256) wpfCrlf=$($wpfCrlfReceipt.contractSha256)"
        }
        Assert-EqualJson -Actual @($browser.contractIds) -Expected @($structure.contractIds) -Label 'Browser contract IDs'
        Assert-EqualJson -Actual @($wpfReceipt.contractIds) -Expected @($structure.contractIds) -Label 'WPF contract IDs'
        Assert-EqualJson -Actual @($wpfCrlfReceipt.contractIds) -Expected @($structure.contractIds) -Label 'WPF CRLF contract IDs'
        Assert-EqualJson -Actual @($browser.caseIds) -Expected @($structure.caseIds) -Label 'Browser case IDs'
        Assert-EqualJson -Actual @($wpfReceipt.caseIds) -Expected @($structure.caseIds) -Label 'WPF case IDs'
        Assert-EqualJson -Actual @($wpfCrlfReceipt.caseIds) -Expected @($structure.caseIds) -Label 'WPF CRLF case IDs'
        if ($browser.casesRun -ne $structure.cases -or $wpfReceipt.casesRun -ne $structure.cases -or $wpfCrlfReceipt.casesRun -ne $structure.cases) {
            throw "Parity case count mismatch: expected=$($structure.cases) browser=$($browser.casesRun) wpf=$($wpfReceipt.casesRun) wpfCrlf=$($wpfCrlfReceipt.casesRun)"
        }
        if (@($browser.failures).Count -ne 0 -or @($wpfReceipt.failures).Count -ne 0 -or @($wpfCrlfReceipt.failures).Count -ne 0) {
            throw "Parity failures remained: Browser=$(@($browser.failures) -join '; ') WPF=$(@($wpfReceipt.failures) -join '; ') WPF-CRLF=$(@($wpfCrlfReceipt.failures) -join '; ')"
        }

        $completed = $true
        [pscustomobject]@{
            ok = $true
            schemaVersion = 1
            contractSha256 = $structure.sha256
            contractIds = @($structure.contractIds)
            caseIds = @($structure.caseIds)
            cases = $structure.cases
            browserFailures = 0
            wpfFailures = 0
            crlfContractHashVerified = $true
            wpfBuildOutput = $artifactsRoot
            artifactsRetained = [bool]$KeepArtifacts
            sourceOrUserStateTouched = $false
        } | ConvertTo-Json -Depth 8
    }
    finally {
        Pop-Location
    }
}
finally {
    [Environment]::SetEnvironmentVariable('PVU_PARITY_BROWSER_RECEIPT_PATH', $previousBrowserReceipt, 'Process')
    if (-not $KeepArtifacts -and (Test-Path -LiteralPath $runRoot)) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        if (-not $resolvedRunRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a parity verifier path outside TEMP: $resolvedRunRoot"
        }
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
    elseif ($KeepArtifacts) {
        Write-Verbose "Parity artifacts retained at $runRoot"
    }
    if (-not $completed -and $KeepArtifacts) {
        Write-Warning "Parity verification failed; artifacts remain at $runRoot"
    }
}

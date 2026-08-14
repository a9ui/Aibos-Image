Set-StrictMode -Version Latest

function Read-AibosJsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Contract file not found: $Path"
    }
    return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Resolve-AibosIndexedPath {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $root = [IO.Path]::GetFullPath($RepoRoot).TrimEnd('\', '/')
    $fullPath = [IO.Path]::GetFullPath((Join-Path $root $RelativePath))
    $rootPrefix = $root + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Indexed contract path escaped the repository: $RelativePath"
    }
    return $fullPath
}

function Get-AibosContractIndex {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    return Read-AibosJsonFile (Resolve-AibosIndexedPath $RepoRoot 'contracts/index.json')
}

function Get-AibosVerificationBundle {
    param(
        [Parameter(Mandatory = $true)][object]$Index,
        [Parameter(Mandatory = $true)][string]$BundleId
    )

    $matches = @($Index.verificationBundles | Where-Object { $_.bundleId -ceq $BundleId })
    if ($matches.Count -ne 1) {
        throw "Expected one verification bundle named $BundleId; found $($matches.Count)."
    }
    return $matches[0]
}

function Get-AibosSharedStateBundle {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    $index = Get-AibosContractIndex $RepoRoot
    $bundle = Get-AibosVerificationBundle $index 'shared-state-v1'
    $members = @($bundle.members | ForEach-Object {
        Read-AibosJsonFile (Resolve-AibosIndexedPath $RepoRoot ([string]$_))
    })
    if ($members.Count -eq 0 -or @($members.id | Select-Object -Unique).Count -ne $members.Count) {
        throw 'Shared-state bundle contract IDs are empty or duplicated.'
    }
    return [pscustomobject][ordered]@{
        schemaVersion = 1
        sourceOfTruth = 'docs/product-contract.md'
        contracts = $members
    }
}

function Get-AibosVideoV1Bundle {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    $index = Get-AibosContractIndex $RepoRoot
    $bundle = Get-AibosVerificationBundle $index 'enhancement-video-v1'
    $core = Read-AibosJsonFile (Resolve-AibosIndexedPath $RepoRoot ([string]$bundle.core))
    $fixture = Read-AibosJsonFile (
        Resolve-AibosIndexedPath $RepoRoot ([string]@($bundle.fixtures)[0]))
    if ($fixture.forContractId -cne $core.contractId) {
        throw 'Video v1 reader fixture ownership does not match its contract.'
    }
    $core | Add-Member -NotePropertyName readerFixture -NotePropertyValue $fixture.readerFixture
    return $core
}

function Get-AibosVideoV2Bundle {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    $index = Get-AibosContractIndex $RepoRoot
    $bundle = Get-AibosVerificationBundle $index 'enhancement-video-v2'
    $core = Read-AibosJsonFile (Resolve-AibosIndexedPath $RepoRoot ([string]$bundle.core))
    $prompt = Read-AibosJsonFile (
        Resolve-AibosIndexedPath $RepoRoot ([string]$bundle.supplementalContract))

    $fixtures = @{}
    foreach ($relativePath in @($bundle.fixtures)) {
        $fixture = Read-AibosJsonFile (Resolve-AibosIndexedPath $RepoRoot ([string]$relativePath))
        if ($fixture.forContractId -cne $core.contractId -and
            $fixture.forContractId -cne $prompt.contractId) {
            throw "Video v2 fixture has an unknown owner: $relativePath"
        }
        if ($fixtures.ContainsKey([string]$fixture.fixtureId)) {
            throw "Duplicate video v2 fixture ID: $($fixture.fixtureId)"
        }
        $fixtures[[string]$fixture.fixtureId] = $fixture
    }

    $promptFixture = $fixtures['PV-ENHANCE-VIDEO-H3-PROMPT-REWRITE-001-FIXTURES']
    foreach ($name in @(
        'sourceFixture',
        'requestFixture',
        'responseFixture',
        'revisionFixtures',
        'errorFixtures'
    )) {
        $prompt | Add-Member -NotePropertyName $name -NotePropertyValue $promptFixture.$name
    }

    $healthFixture = $fixtures['PV-ENHANCE-VIDEO-002-HEALTH-FIXTURES']
    $core.passiveHealthGate | Add-Member -NotePropertyName readyShape -NotePropertyValue $healthFixture.readyShape
    $core.passiveHealthGate | Add-Member -NotePropertyName notReadyShape -NotePropertyValue $healthFixture.notReadyShape

    $readerFixture = $fixtures['PV-ENHANCE-VIDEO-002-READER-FIXTURES']
    $core | Add-Member -NotePropertyName promptRewriteProtocol -NotePropertyValue $prompt
    $core | Add-Member -NotePropertyName readerFixture -NotePropertyValue $readerFixture.readerFixture
    return $core
}

function Write-AibosJsonFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object]$Value
    )

    $json = ConvertTo-Json -InputObject $Value -Depth 100
    [IO.File]::WriteAllText(
        [IO.Path]::GetFullPath($Path),
        $json + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
}

function Test-AibosContractIndex {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    $index = Get-AibosContractIndex $RepoRoot
    if ($index.schemaVersion -ne 1 -or
        $index.semanticAuthority -cne 'docs/product-contract.md') {
        throw 'Contract index identity is invalid.'
    }

    $contractIds = @($index.contracts | ForEach-Object { [string]$_.contractId })
    $contractPaths = @($index.contracts | ForEach-Object { [string]$_.path })
    if (@($contractIds | Select-Object -Unique).Count -ne $contractIds.Count -or
        @($contractPaths | Select-Object -Unique).Count -ne $contractPaths.Count) {
        throw 'Contract index IDs or paths are duplicated.'
    }

    foreach ($entry in @($index.contracts)) {
        $document = Read-AibosJsonFile (
            Resolve-AibosIndexedPath $RepoRoot ([string]$entry.path))
        $actualId = if ($null -ne $document.PSObject.Properties['contractId']) {
            [string]$document.contractId
        }
        elseif ($null -ne $document.PSObject.Properties['id']) {
            [string]$document.id
        }
        elseif ($null -ne $document.PSObject.Properties['contract']) {
            [string]$document.contract
        }
        else {
            ''
        }
        if ($actualId -cne [string]$entry.contractId) {
            throw "Contract index identity mismatch for $($entry.path)."
        }
    }

    $fixtureIds = @($index.fixtures | ForEach-Object { [string]$_.fixtureId })
    if (@($fixtureIds | Select-Object -Unique).Count -ne $fixtureIds.Count) {
        throw 'Contract fixture IDs are duplicated.'
    }
    foreach ($entry in @($index.fixtures)) {
        $fixture = Read-AibosJsonFile (
            Resolve-AibosIndexedPath $RepoRoot ([string]$entry.path))
        if ($fixture.fixtureId -cne [string]$entry.fixtureId -or
            $fixture.forContractId -cne [string]$entry.forContractId -or
            $contractIds -notcontains [string]$entry.forContractId) {
            throw "Contract fixture index mismatch for $($entry.path)."
        }
    }

    $shared = Get-AibosSharedStateBundle $RepoRoot
    $videoV1 = Get-AibosVideoV1Bundle $RepoRoot
    $videoV2 = Get-AibosVideoV2Bundle $RepoRoot
    if (@($shared.contracts).Count -ne 6 -or
        $videoV1.contractId -cne 'PV-ENHANCE-VIDEO-001' -or
        $videoV2.contractId -cne 'PV-ENHANCE-VIDEO-002' -or
        $videoV2.promptRewriteProtocol.contractId -cne
            'PV-ENHANCE-VIDEO-H3-PROMPT-REWRITE-001') {
        throw 'A verification bundle could not be materialized with its expected identity.'
    }

    return [pscustomobject]@{
        ok = $true
        contracts = $contractIds.Count
        fixtures = $fixtureIds.Count
        bundles = @($index.verificationBundles).Count
        sharedStateMembers = @($shared.contracts).Count
    }
}

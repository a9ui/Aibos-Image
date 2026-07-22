param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$contract = Join-Path $repoRoot 'contracts\shared-root-locator-v1.json'
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
$runRoot = Join-Path $tempBase ('aibos-shared-root-locator-' + [guid]::NewGuid().ToString('N'))
$fixtureRoot = Join-Path $runRoot 'fixture'
$resultPath = Join-Path $runRoot 'result.json'
$buildOutput = Join-Path $runRoot 'build'

try {
    New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
    & dotnet build $project -c $Configuration "-p:OutputPath=$buildOutput" --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) {
        throw "WPF build failed with exit $LASTEXITCODE."
    }

    $dll = Join-Path $buildOutput 'PhotoViewer.Wpf.dll'
    & dotnet $dll --shared-root-locator-smoke $resultPath --contract $contract --temp-root $fixtureRoot
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Shared root locator smoke failed with exit $exitCode."
    }
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        throw 'Shared root locator smoke produced no result.'
    }

    $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    $failedCases = @($result.cases | Where-Object { $_.ok -ne $true })
    if ($result.ok -ne $true -or
        $result.activation -ne 'reader-only' -or
        $result.defaultPathShapeOk -ne $true -or
        $result.caseCount -ne 22 -or
        $failedCases.Count -ne 0) {
        $failedIds = @($failedCases | ForEach-Object { $_.id }) -join ', '
        throw "Shared root locator contract failed. Failed cases: $failedIds"
    }

    $changedTrees = @($result.cases | Where-Object { $_.treeUnchanged -ne $true })
    $implicitLocators = @($result.cases | Where-Object { $_.noImplicitLocatorCreated -ne $true })
    $splitRoots = @($result.cases | Where-Object { $_.crossLocationMatches -ne $true })
    if ($changedTrees.Count -ne 0 -or
        $implicitLocators.Count -ne 0 -or
        $splitRoots.Count -ne 0) {
        throw 'The reader changed a fixture or created a missing locator.'
    }

    Write-Host "Shared root locator PASS: $($result.caseCount)/$($result.caseCount) cases; reader-only; fixture bytes unchanged."
}
finally {
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        $tempPrefix = $tempBase + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedRunRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing cleanup outside TEMP.'
        }
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\Aibos.SharedRootSetup\Aibos.SharedRootSetup.csproj'
$artifacts = Join-Path $env:TEMP ("aibos-shared-root-setup-build-" + [guid]::NewGuid().ToString('N'))
$result = Join-Path $env:TEMP ("aibos-shared-root-setup-result-" + [guid]::NewGuid().ToString('N') + '.json')
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$installedSdks = & $dotnet --list-sdks
if (-not ($installedSdks -match '^10\.')) {
    $localDotnet10 = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet10\dotnet.exe'
    if (-not (Test-Path -LiteralPath $localDotnet10)) {
        throw '.NET 10 SDK is required.'
    }
    $dotnet = $localDotnet10
}

try {
    & $dotnet build $project -c Release --artifacts-path $artifacts --nologo
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $setup = Get-ChildItem -Path $artifacts -Filter 'Aibos.SharedRootSetup.dll' -File -Recurse |
        Where-Object { $_.FullName -like '*\bin\Aibos.SharedRootSetup\release\*' } |
        Select-Object -First 1
    if (-not $setup) {
        throw 'Aibos.SharedRootSetup.dll was not produced.'
    }

    & $dotnet $setup.FullName --smoke $result
    $exitCode = $LASTEXITCODE
    if (Test-Path -LiteralPath $result) {
        Get-Content -Raw -LiteralPath $result
    }
    exit $exitCode
}
finally {
    if (Test-Path -LiteralPath $result) {
        Remove-Item -LiteralPath $result -Force
    }
    $resolvedTemp = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
    $resolvedArtifacts = [IO.Path]::GetFullPath($artifacts)
    if ($resolvedArtifacts.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedArtifacts -PathType Container)) {
        Remove-Item -LiteralPath $resolvedArtifacts -Recurse -Force
    }
}

param(
    [string]$Configuration = 'Release',
    [string]$OutputPath = (Join-Path $env:TEMP ('photoviewer-wpf-prompt-tag-search-' + [guid]::NewGuid().ToString('N') + '.json'))
)

$ErrorActionPreference = 'Stop'
if ($OutputPath.Contains('"')) { throw 'OutputPath cannot contain a double quote.' }

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net8.0-windows\PhotoViewer.Wpf.exe"

dotnet build $project -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Remove-Item -LiteralPath $OutputPath -ErrorAction SilentlyContinue
$process = Start-Process -FilePath $exe `
    -ArgumentList @('--prompt-tag-search-smoke', ('"{0}"' -f $OutputPath)) `
    -WindowStyle Hidden -Wait -PassThru

if (-not (Test-Path -LiteralPath $OutputPath)) {
    throw "WPF prompt tag search process exited without producing $OutputPath"
}

$result = Get-Content -Raw -LiteralPath $OutputPath | ConvertFrom-Json
$failures = @()
if ($process.ExitCode -ne 0) { $failures += "process exit code $($process.ExitCode)" }
if ($result.ok -ne $true) { $failures += 'result.ok was false' }
if (($result.initialTags -join '|') -ne 'studio portrait|soft light') { $failures += 'prompt tags were not trimmed, deduped, and kept in stable order' }
if ($result.appended.searchQuery -ne 'studio portrait, soft light') { $failures += 'tag was not appended as a stable comma query' }
if ($result.appended.modalVisible -ne $false -or $result.appended.searchFocused -ne $true) { $failures += 'modal close or search focus failed' }
if ($result.initialAccessibilityReady -ne $true) { $failures += 'prompt chip accessibility metadata is missing' }
if ($result.searchPersisted -ne $true) { $failures += 'search query did not persist and reload' }
if ($result.promptFallbackVisible -ne $true) { $failures += 'prompt chip fallback is not visible when metadata has no prompt' }
if ($result.promptEmphasisNormalized -ne $true) { $failures += 'prompt display retained emphasis wrapper parentheses or brackets' }
if ($result.sourceUntouched -ne $true) { $failures += 'source image changed' }
if ($result.enhancementJobsBefore -ne $result.enhancementJobsAfter -or $result.enhancementCandidatesBefore -ne $result.enhancementCandidatesAfter) { $failures += 'enhancement state changed' }

$result | ConvertTo-Json -Depth 8
if ($failures.Count -gt 0) {
    throw ('WPF prompt tag search gate failed: ' + ($failures -join '; '))
}

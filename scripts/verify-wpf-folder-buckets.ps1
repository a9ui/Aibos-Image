param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputPath = (Join-Path $env:TEMP ('photoviewer-wpf-folder-buckets-' + [guid]::NewGuid().ToString('N') + '.json')),
    [ValidateRange(10, 300)]
    [int]$TimeoutSeconds = 90,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net8.0-windows\PhotoViewer.Wpf.exe"
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
if (-not $outputFullPath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputPath must stay under TEMP: $outputFullPath"
}
if ($outputFullPath.Contains('"')) { throw 'OutputPath cannot contain a double quote.' }

if (-not $NoBuild) {
    dotnet build $project -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (-not (Test-Path -LiteralPath $exe)) { throw "WPF executable is missing: $exe" }

$process = $null
try {
    # Output is always an exact, resolved TEMP file.  An explicitly supplied
    # path is retained for inspection after the gate completes.
    Remove-Item -LiteralPath $outputFullPath -Force -ErrorAction SilentlyContinue
    $process = Start-Process -FilePath $exe `
        -ArgumentList @('--folder-bucket-smoke', ('"{0}"' -f $outputFullPath)) `
        -WindowStyle Hidden `
        -PassThru

    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        try { $process.Kill($true) } catch { }
        try { $process.WaitForExit(5000) | Out-Null } catch { }
        throw "WPF folder-bucket PID $($process.Id) exceeded ${TimeoutSeconds}s; its exact process tree was terminated"
    }
    $process.Refresh()

    if (-not (Test-Path -LiteralPath $outputFullPath)) {
        throw "WPF folder-bucket process exited without producing $outputFullPath"
    }
    try {
        $result = Get-Content -Raw -LiteralPath $outputFullPath | ConvertFrom-Json
    }
    catch {
        throw "WPF folder-bucket result was not readable JSON: $($_.Exception.Message)"
    }

    $failures = [Collections.Generic.List[string]]::new()
    if ($process.ExitCode -ne 0) { $failures.Add("process exit code $($process.ExitCode)") }
    if ($result.ok -ne $true) { $failures.Add('result.ok was false') }
    if ($result.folderAutomationRoundTrip -ne $true) { $failures.Add('folder AutomationPeer round trip failed') }
    if ($result.folderAutomationInitialState -ne 'Expanded') { $failures.Add("initial UIA state was $($result.folderAutomationInitialState)") }
    if ($result.folderAutomationCollapsedState -ne 'Collapsed') { $failures.Add("collapsed UIA state was $($result.folderAutomationCollapsedState)") }
    if ($result.folderAutomationExpandedState -ne 'Expanded') { $failures.Add("restored UIA state was $($result.folderAutomationExpandedState)") }
    if ($result.selectedActionsOk -ne $true) { $failures.Add('selected folder-bucket actions failed') }
    if ($result.invalidLegacyFallback -ne $true) { $failures.Add('invalid legacy state fallback failed') }

    $result | ConvertTo-Json -Depth 8
    if ($failures.Count -gt 0) {
        throw ('WPF folder-bucket gate failed: ' + ($failures -join '; '))
    }
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        try { $process.Kill($true) } catch { }
        try { $process.WaitForExit(5000) | Out-Null } catch { }
    }
}

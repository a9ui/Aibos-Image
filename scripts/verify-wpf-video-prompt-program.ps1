param(
    [string]$AssemblyPath = '',
    [ValidateRange(10, 180)][int]$TimeoutSeconds = 60
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('aibos-video-prompt-verifier-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
$resultPath = Join-Path $runRoot 'result.json'
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$process = $null
$oldScratch = $env:NUGET_SCRATCH
try {
    if ([string]::IsNullOrEmpty($AssemblyPath)) {
        $artifacts = Join-Path $runRoot 'build'
        $env:NUGET_SCRATCH = Join-Path $runRoot 'nuget-scratch'
        & $dotnet build (Join-Path $repoRoot 'local-native/PhotoViewer.Wpf/PhotoViewer.Wpf.csproj') -c Release --artifacts-path $artifacts --nologo --disable-build-servers -p:UseSharedCompilation=false
        if ($LASTEXITCODE -ne 0) { throw 'Source build failed.' }
        $AssemblyPath = Join-Path $artifacts 'bin/PhotoViewer.Wpf/release/PhotoViewer.Wpf.dll'
    }
    $assembly = (Resolve-Path -LiteralPath $AssemblyPath).Path
    $process = Start-Process -FilePath $dotnet -ArgumentList @(('"{0}"' -f $assembly), '--video-prompt-program-smoke', ('"{0}"' -f $resultPath)) -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill()
        throw 'The task-owned synthetic smoke exceeded its timeout.'
    }
    $process.WaitForExit()
    if (-not (Test-Path -LiteralPath $resultPath)) { throw 'No smoke result was produced.' }
    $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    if ($process.ExitCode -ne 0 -or -not $result.ok) { throw ($result | ConvertTo-Json -Depth 8) }
    [pscustomobject]@{ ok = $true; resultPath = $resultPath; renderPath = (Join-Path $runRoot 'prompt-editor.png'); checks = $result.checks } | ConvertTo-Json -Depth 8
}
finally {
    $env:NUGET_SCRATCH = $oldScratch
    if ($null -ne $process) { $process.Dispose() }
    # Keep the small synthetic evidence for review and handoff.
}

[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Only bounded synthetic children under this unique TEMP directory are used.
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('aibos-detachment-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
$helper = Join-Path $PSScriptRoot 'start-wpf-detached.ps1'
$powerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$localDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet10\dotnet.exe'
if (Test-Path -LiteralPath $localDotnet) { $dotnet = $localDotnet }
[IO.File]::WriteAllText((Join-Path $runRoot 'Fixture.csproj'), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>', $utf8)
[IO.File]::WriteAllText((Join-Path $runRoot 'Program.cs'), @'
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
class Program {
    [DllImport("kernel32.dll")] static extern uint GetConsoleProcessList(uint[] ids, uint count);
    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    static int Main(string[] args) {
        if (args.Length == 1 && args[0] == "--fail") return 7;
        var consoleIds = new uint[8];
        var consoleCount = GetConsoleProcessList(consoleIds, 8);
        File.WriteAllText(args[0], JsonSerializer.Serialize(new {
            pid = Environment.ProcessId, arguments = args.Skip(1).ToArray(),
            consoleProcesses = consoleCount, consoleIds, consoleWindow = GetConsoleWindow().ToInt64()
        }));
        var deadline = DateTime.UtcNow.AddSeconds(35);
        while (!File.Exists(args[0] + ".stop") && DateTime.UtcNow < deadline) Thread.Sleep(100);
        return 0;
    }
}
'@, $utf8)
$savedScratch = $env:NUGET_SCRATCH
try {
    $env:NUGET_SCRATCH = Join-Path $runRoot 'nuget-scratch'
    & $dotnet build (Join-Path $runRoot 'Fixture.csproj') -c Release --nologo --disable-build-servers -p:UseSharedCompilation=false
} finally { $env:NUGET_SCRATCH = $savedScratch }
if ($LASTEXITCODE -ne 0) { throw 'Synthetic fixture build failed.' }
$targetRoot = Join-Path $runRoot 'bin\Release\net10.0-windows'
$payload = @('space name', '-dash-value', 'quote"inside', 'C:\trailing\', '', ([string][char]0x753B + [char]0x50CF))
$results = [Collections.Generic.List[string]]::new()
foreach ($hostMode in @('apphost', 'dotnet')) {
    foreach ($parentMode in @('exit', 'close')) {
        $case = "$hostMode-$parentMode"
        $report = Join-Path $runRoot ($case + '.json')
        $request = Join-Path $runRoot ($case + '.ps1')
        $launchArgs = @(if ($hostMode -eq 'apphost') { (Join-Path $targetRoot 'Fixture.exe') } else { $dotnet; (Join-Path $targetRoot 'Fixture.dll') })
        $launchArgs += @($report) + $payload
        $literalArgs = ($launchArgs | ForEach-Object { "'" + $_.Replace("'", "''") + "'" }) -join ', '
        $runtimeRoot = (Split-Path -Parent $dotnet).Replace("'", "''")
        $requestBody = "`$env:DOTNET_ROOT = '$runtimeRoot'`r`n`$env:DOTNET_ROOT_X64 = '$runtimeRoot'`r`n"
        $requestBody += "`$launchArgs = @($literalArgs)`r`n& '" + $helper.Replace("'", "''") + "' @launchArgs`r`nexit `$LASTEXITCODE`r`n"
        [IO.File]::WriteAllText($request, $requestBody, [Text.UTF8Encoding]::new($true))
        $switch = if ($parentMode -eq 'exit') { '/c' } else { '/k' }
        $parent = Start-Process -FilePath $env:ComSpec -ArgumentList ('/d /s {0} ""{1}" -NoProfile -ExecutionPolicy Bypass -File "{2}""' -f $switch, $powerShell, $request) -WindowStyle Hidden -PassThru
        $child = $null
        try {
            $deadline = [DateTime]::UtcNow.AddSeconds(15)
            while (-not (Test-Path -LiteralPath $report)) {
                if ([DateTime]::UtcNow -gt $deadline) { throw "$case did not start." }
                Start-Sleep -Milliseconds 100
            }
            $data = [IO.File]::ReadAllText($report) | ConvertFrom-Json
            $child = [Diagnostics.Process]::GetProcessById($data.pid)
            $null = $child.Handle
            # Some Windows versions expose a one-process headless console for
            # CREATE_NO_WINDOW. It must have no window and no shared parent.
            if ($data.consoleWindow -ne 0 -or $data.consoleProcesses -gt 1 -or
                ($data.consoleProcesses -eq 1 -and $data.consoleIds[0] -ne $data.pid)) {
                throw "$case has a console window or shares its launcher's console."
            }
            if (($data.arguments | ConvertTo-Json -Compress) -cne ($payload | ConvertTo-Json -Compress)) { throw "$case altered application arguments." }
            if ($parentMode -eq 'exit') {
                if (-not $parent.WaitForExit(8000)) { throw 'CMD waited for the application lifetime.' }
                if ($parent.ExitCode -ne 0) { throw 'CMD reported a startup error.' }
            } else {
                Start-Sleep -Milliseconds 1500
                $parent.Kill()
                $parent.WaitForExit()
            }
            Start-Sleep -Milliseconds 300
            if ($child.HasExited) { throw "$case stopped with its launch parent." }
            if ($child.PriorityClass -ne [Diagnostics.ProcessPriorityClass]::Normal) { throw "$case did not use normal priority." }
            $results.Add($case)
        }
        finally {
            [IO.File]::WriteAllText($report + '.stop', '', $utf8)
            if ($null -ne $child) {
                if (-not $child.WaitForExit(3000)) { $child.Kill() }
                $child.Dispose()
            }
            if (-not $parent.HasExited) { $parent.Kill(); $parent.WaitForExit() }
            $parent.Dispose()
        }
    }
}
& $powerShell -NoProfile -ExecutionPolicy Bypass -File $helper $dotnet (Join-Path $targetRoot 'Fixture.dll') --fail
if ($LASTEXITCODE -ne 7) { throw 'Immediate startup failure was hidden.' }
$results.Add('immediate-error-preserved')
[pscustomobject]@{ ok = $true; checks = @($results); consoleIndependent = $true; argumentRoundtrip = $true; syntheticOnly = $true; fixtureRoot = $runRoot } | ConvertTo-Json

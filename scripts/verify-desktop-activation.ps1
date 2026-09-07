[CmdletBinding()]
param([string]$DotnetPath = 'dotnet')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('aibos-activation-verifier-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
$source = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\SingleInstanceCoordinator.cs'
Copy-Item -LiteralPath $source -Destination (Join-Path $runRoot 'SingleInstanceCoordinator.cs')
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $runRoot 'Fixture.csproj'), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>', $utf8)
$program = @'
using System.Diagnostics;
using PhotoViewer.Wpf;

string identity = Guid.NewGuid().ToString("N");
string helper = args[0].Replace("'", "''");
int Send(string target)
{
    using var process = new Process();
    process.StartInfo = new ProcessStartInfo("powershell") { UseShellExecute = false, CreateNoWindow = true };
    foreach (string argument in new[] { "-NoProfile", "-Command", $"$ErrorActionPreference='Stop'; . '{helper}'; if (Send-AibosDesktopActivation -Identity 'smoke-{target}') {{ exit 0 }} else {{ exit 10 }}" })
        process.StartInfo.ArgumentList.Add(argument);
    process.Start();
    if (!process.WaitForExit(10000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("Activation helper timed out."); }
    return process.ExitCode;
}
if (Send(identity) != 10) throw new Exception("Absent primary was treated as present.");
using (var primary = SingleInstanceCoordinator.CreateForSmoke(identity))
{
    using var activated = new AutoResetEvent(false);
    primary.StartListening(() => activated.Set());
    if (Send(Guid.NewGuid().ToString("N")) != 10 || activated.WaitOne(100))
        throw new Exception("A different identity was activated.");
    for (int attempt = 0; attempt < 2; attempt++)
        if (Send(identity) != 0 || !activated.WaitOne(2000)) throw new Exception("Repeat activation was lost.");
}
if (Send(identity) != 10) throw new Exception("Exited primary was treated as present.");
Console.WriteLine("{\"ok\":true,\"csharpInterop\":true,\"repeatActivation\":true,\"identityIsolation\":true,\"syntheticOnly\":true}");
'@
[IO.File]::WriteAllText((Join-Path $runRoot 'Program.cs'), $program, $utf8)
& $DotnetPath build (Join-Path $runRoot 'Fixture.csproj') -c Release --nologo --disable-build-servers -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw 'Activation fixture build failed.' }
& $DotnetPath (Join-Path $runRoot 'bin\Release\net10.0-windows\Fixture.dll') (Join-Path $PSScriptRoot 'lib\DesktopActivation.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Activation interoperability verification failed.' }
Write-Output "fixtureRoot=$runRoot"

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const int EnhancementCompanionReadyTimeoutMilliseconds = 120_000;
    private const int EnhancementCompanionProbeDelayMilliseconds = 450;

    private readonly SemaphoreSlim _enhancementCompanionLaunchGate = new(1, 1);
    private readonly CancellationTokenSource _enhancementCompanionLifetimeCts = new();
    private Process? _ownedEnhancementCompanion;
    private string? _enhancementCompanionLaunchError;
    private int _enhancementCompanionLaunchAttemptCount;
    private Func<Uri, (bool Started, string Error)>? _startEnhancementCompanionForSmoke;

    private async Task<EnhancementApiResponse> EnsureEnhancementCompanionReadyForExplicitActionAsync(
        string? sourceIdentity = null,
        CancellationToken token = default)
    {
        string readinessRoute = string.IsNullOrWhiteSpace(sourceIdentity)
            ? "api/enhance/jobs"
            : $"api/enhance/jobs?sourceId={Uri.EscapeDataString(sourceIdentity)}";
        EnhancementApiResponse response = await SendEnhancementApiAsync(
            HttpMethod.Get,
            readinessRoute,
            token: token);
        if (IsReadyEnhancementCompanionResponse(response) || !_usingDefaultModalEnhancementSender)
            return response;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            token,
            _enhancementCompanionLifetimeCts.Token);
        CancellationToken linkedToken = linkedCts.Token;
        await _enhancementCompanionLaunchGate.WaitAsync(linkedToken);
        try
        {
            response = await SendEnhancementApiAsync(
                HttpMethod.Get,
                readinessRoute,
                token: linkedToken);
            if (IsReadyEnhancementCompanionResponse(response))
                return response;

            if (_ownedEnhancementCompanion is null || _ownedEnhancementCompanion.HasExited)
            {
                StopOwnedEnhancementCompanion();
                if (!TryStartOwnedEnhancementCompanion(out string startError))
                {
                    _enhancementCompanionLaunchError = startError;
                    return new EnhancementApiResponse(false, 0, null, startError);
                }
            }

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(EnhancementCompanionReadyTimeoutMilliseconds);
            while (DateTime.UtcNow < deadline)
            {
                linkedToken.ThrowIfCancellationRequested();
                if (_ownedEnhancementCompanion is { HasExited: true } exited)
                {
                    string error = $"The local AI companion stopped before it became ready (exit {exited.ExitCode}).";
                    _enhancementCompanionLaunchError = error;
                    StopOwnedEnhancementCompanion();
                    return new EnhancementApiResponse(false, 0, null, error);
                }

                await Task.Delay(EnhancementCompanionProbeDelayMilliseconds, linkedToken);
                response = await SendEnhancementApiAsync(
                    HttpMethod.Get,
                    readinessRoute,
                    token: linkedToken);
                if (IsReadyEnhancementCompanionResponse(response))
                {
                    _enhancementCompanionLaunchError = null;
                    return response;
                }
            }

            string timeoutError = "The local AI companion did not become ready within two minutes.";
            _enhancementCompanionLaunchError = timeoutError;
            StopOwnedEnhancementCompanion();
            return new EnhancementApiResponse(false, 0, null, timeoutError);
        }
        catch (OperationCanceledException)
        {
            StopOwnedEnhancementCompanion();
            return new EnhancementApiResponse(false, 0, null, "Starting the local AI companion was canceled.");
        }
        finally
        {
            _enhancementCompanionLaunchGate.Release();
        }
    }

    private static bool IsReadyEnhancementCompanionResponse(EnhancementApiResponse response)
        => response.Ok
            && response.Payload is JsonElement payload
            && payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("jobs", out JsonElement jobs)
            && jobs.ValueKind == JsonValueKind.Array;

    private bool TryStartOwnedEnhancementCompanion(out string error)
    {
        error = "";
        Uri endpoint = ResolveBrowserEnhancementBaseUri();
        if (_startEnhancementCompanionForSmoke is not null)
        {
            (bool started, string injectedError) = _startEnhancementCompanionForSmoke(endpoint);
            if (started)
            {
                _enhancementCompanionLaunchAttemptCount++;
                return true;
            }
            error = injectedError;
            return false;
        }

        string? root = ResolveEnhancementCompanionRoot();
        if (root is null)
        {
            error = "Aibos could not find the H25 Browser companion beside this portable build. Open the H25 copy of Aibos or set AIBOS_H25_COMPANION_ROOT.";
            return false;
        }

        try
        {
            if (endpoint.Scheme != Uri.UriSchemeHttp
                || !string.Equals(endpoint.Host, "127.0.0.1", StringComparison.Ordinal))
            {
                error = "Automatic local AI startup requires an http://127.0.0.1 loopback endpoint.";
                return false;
            }
            string? nodeExecutable = ResolveNodeExecutablePath();
            if (nodeExecutable is null)
            {
                error = "Aibos could not find an installed Node.js executable for the local AI companion.";
                return false;
            }
            ProcessStartInfo startInfo = CreateEnhancementCompanionStartInfo(
                nodeExecutable,
                root,
                endpoint);

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = false,
            };
            if (!process.Start())
            {
                process.Dispose();
                error = "Windows did not start the local AI companion.";
                return false;
            }

            _ownedEnhancementCompanion = process;
            _enhancementCompanionLaunchAttemptCount++;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            error = $"Aibos could not start the local AI companion: {ex.Message}";
            return false;
        }
    }

    private static ProcessStartInfo CreateEnhancementCompanionStartInfo(
        string nodeExecutable,
        string root,
        Uri endpoint)
    {
        var startInfo = new ProcessStartInfo
        {
            // ResolveNodeExecutablePath only accepts canonical node.exe files
            // below the Windows Program Files roots.
            // codeql[cs/command-line-injection]
            FileName = nodeExecutable,
            // root is a canonical H25 root whose package/project identity and
            // contained production launcher were validated before this call.
            // codeql[cs/command-line-injection]
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        startInfo.ArgumentList.Add(Path.Combine(root, "scripts", "prod_launcher.js"));
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(endpoint.Port.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        startInfo.Environment["PVU_NO_OPEN"] = "1";
        startInfo.Environment["PVU_COMFY_AUTOSTART"] = "0";
        // Do not set PVU_OWNER_PID. The companion owns the durable FIFO worker
        // after an explicit AI action and must outlive the WPF viewer process.
        return startInfo;
    }

    private static string? ResolveEnhancementCompanionRoot()
        => ResolveEnhancementCompanionRoot(
            Environment.GetEnvironmentVariable("AIBOS_H25_COMPANION_ROOT"),
            AppContext.BaseDirectory);

    private static string? ResolveEnhancementCompanionRoot(
        string? configuredRoot,
        string appBaseDirectory)
    {
        // An explicitly configured root is authoritative and must itself be
        // the H25 project root. Never walk its parents or silently fall back.
        if (!string.IsNullOrWhiteSpace(configuredRoot))
            return TryValidateEnhancementCompanionRoot(configuredRoot, out string configured)
                ? configured
                : null;

        string? current;
        try
        {
            current = Path.GetFullPath(appBaseDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }

        // Portable builds can live below the H25 root. AppContext.BaseDirectory
        // is controlled by the launched app, unlike Environment.CurrentDirectory.
        for (int depth = 0; depth < 12 && current is not null; depth++)
        {
            if (TryValidateEnhancementCompanionRoot(current, out string validated))
                return validated;
            current = Directory.GetParent(current)?.FullName;
        }
        return null;
    }

    private static bool TryValidateEnhancementCompanionRoot(
        string candidateRoot,
        out string validatedRoot)
    {
        validatedRoot = "";
        try
        {
            string lexicalRoot = Path.GetFullPath(candidateRoot);
            if (!Directory.Exists(lexicalRoot))
                return false;

            string canonicalRoot = ResolveFinalPathCore(lexicalRoot);
            if (!Directory.Exists(canonicalRoot))
                return false;

            string packagePath = Path.Combine(canonicalRoot, "package.json");
            string projectPath = Path.Combine(canonicalRoot, "project.toml");
            string launcherPath = Path.Combine(canonicalRoot, "scripts", "prod_launcher.js");
            foreach (string requiredPath in new[] { packagePath, projectPath, launcherPath })
            {
                if (!File.Exists(requiredPath))
                    return false;
                string canonicalRequiredPath = ResolveFinalPathCore(requiredPath);
                if (!IsPathInside(canonicalRequiredPath, canonicalRoot))
                    return false;
            }

            using JsonDocument package = JsonDocument.Parse(File.ReadAllText(packagePath));
            JsonElement packageRoot = package.RootElement;
            bool packageIdentity = packageRoot.ValueKind == JsonValueKind.Object
                && packageRoot.TryGetProperty("name", out JsonElement name)
                && name.ValueKind == JsonValueKind.String
                && string.Equals(
                    name.GetString(),
                    "h000025-photoviewer",
                    StringComparison.Ordinal)
                && packageRoot.TryGetProperty("private", out JsonElement privateValue)
                && privateValue.ValueKind is JsonValueKind.True;
            if (!packageIdentity)
                return false;

            string[] projectLines = File.ReadAllLines(projectPath);
            bool projectId = projectLines.Any(static line =>
                string.Equals(line.Trim(), "id = \"H000025\"", StringComparison.Ordinal));
            bool projectName = projectLines.Any(static line =>
                string.Equals(line.Trim(), "name = \"PhotoViewer\"", StringComparison.Ordinal));
            if (!projectId || !projectName)
                return false;

            validatedRoot = canonicalRoot;
            return true;
        }
        catch (Exception ex) when (ex is
            ArgumentException or
            NotSupportedException or
            UnauthorizedAccessException or
            IOException or
            JsonException)
        {
            return false;
        }
    }

    private static string? ResolveNodeExecutablePath()
    {
        var candidates = new List<string>();
        string? programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
            candidates.Add(Path.Combine(programFiles, "nodejs", "node.exe"));
        string? programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
            candidates.Add(Path.Combine(programFilesX86, "nodejs", "node.exe"));

        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string fullPath = Path.GetFullPath(candidate);
                if (File.Exists(fullPath))
                    return ResolveFinalPathCore(fullPath);
            }
            catch (Exception ex) when (ex is
                ArgumentException or
                NotSupportedException or
                UnauthorizedAccessException or
                IOException)
            {
            }
        }
        return null;
    }

    private void StopOwnedEnhancementCompanion()
    {
        Process? process = Interlocked.Exchange(ref _ownedEnhancementCompanion, null);
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Only the exact process tree created by this WPF instance is owned.
        }
        finally
        {
            process.Dispose();
        }
    }

    private void ReleaseOwnedEnhancementCompanion()
    {
        // Disposing a Process wrapper does not stop the OS process. Once the
        // loopback companion is ready, it is an independent durable worker so
        // queued/running jobs continue while Aibos is closed.
        Interlocked.Exchange(ref _ownedEnhancementCompanion, null)?.Dispose();
    }

    private void CancelOwnedEnhancementCompanionLifetime()
    {
        try
        {
            _enhancementCompanionLifetimeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public int EnhancementCompanionLaunchAttemptCountForSmoke => _enhancementCompanionLaunchAttemptCount;
    public string? EnhancementCompanionLaunchErrorForSmoke => _enhancementCompanionLaunchError;
    public static string? ResolveEnhancementCompanionRootForSmoke() => ResolveEnhancementCompanionRoot();
    public static string? ResolveEnhancementCompanionRootForSmoke(
        string? configuredRoot,
        string appBaseDirectory)
        => ResolveEnhancementCompanionRoot(configuredRoot, appBaseDirectory);
    public static string? ResolveNodeExecutablePathForSmoke() => ResolveNodeExecutablePath();
    public static EnhancementCompanionLaunchContractSmokeSnapshot
        EnhancementCompanionLaunchContractForSmoke()
        => new(
            UseShellExecute: false,
            CreateNoWindow: true,
            RedirectStandardOutput: false,
            RedirectStandardError: false,
            HasExternalOwnerPid: false,
            NoOpen: "1",
            ComfyAutostart: "0");
    public void ConfigureEnhancementCompanionAutoStartForSmoke(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sender,
        Func<Uri, (bool Started, string Error)> starter)
    {
        _modalEnhancementSender = sender;
        _usingDefaultModalEnhancementSender = true;
        _startEnhancementCompanionForSmoke = starter;
    }

    public async Task<bool> EnsureEnhancementCompanionForExplicitActionForSmokeAsync()
    {
        EnhancementApiResponse response = await EnsureEnhancementCompanionReadyForExplicitActionAsync();
        return response.Ok;
    }
}

public sealed record EnhancementCompanionLaunchContractSmokeSnapshot(
    bool UseShellExecute,
    bool CreateNoWindow,
    bool RedirectStandardOutput,
    bool RedirectStandardError,
    bool HasExternalOwnerPid,
    string? NoOpen,
    string? ComfyAutostart);

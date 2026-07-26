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
            var startInfo = new ProcessStartInfo
            {
                FileName = "node.exe",
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add(Path.Combine(root, "scripts", "prod_launcher.js"));
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.Environment["PVU_NO_OPEN"] = "1";
            startInfo.Environment["PVU_COMFY_AUTOSTART"] = "0";
            startInfo.Environment["PVU_OWNER_PID"] = Environment.ProcessId.ToString(
                System.Globalization.CultureInfo.InvariantCulture);

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true,
            };
            process.OutputDataReceived += OwnedEnhancementCompanion_OutputDataReceived;
            process.ErrorDataReceived += OwnedEnhancementCompanion_OutputDataReceived;
            if (!process.Start())
            {
                process.Dispose();
                error = "Windows did not start the local AI companion.";
                return false;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
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

    private void OwnedEnhancementCompanion_OutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
            return;

        string line = e.Data.Trim();
        if (line.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            _enhancementCompanionLaunchError = line.Length <= 320 ? line : line[^320..];
        }
    }

    private static string? ResolveEnhancementCompanionRoot()
    {
        var starts = new List<string>();
        string? configured = Environment.GetEnvironmentVariable("AIBOS_H25_COMPANION_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
            starts.Add(configured);
        starts.Add(AppContext.BaseDirectory);
        starts.Add(Environment.CurrentDirectory);

        foreach (string start in starts)
        {
            string? current;
            try
            {
                current = Path.GetFullPath(start);
            }
            catch
            {
                continue;
            }

            for (int depth = 0; depth < 12 && current is not null; depth++)
            {
                if (File.Exists(Path.Combine(current, "package.json"))
                    && File.Exists(Path.Combine(current, "scripts", "prod_launcher.js")))
                {
                    return current;
                }
                current = Directory.GetParent(current)?.FullName;
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
            process.OutputDataReceived -= OwnedEnhancementCompanion_OutputDataReceived;
            process.ErrorDataReceived -= OwnedEnhancementCompanion_OutputDataReceived;
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

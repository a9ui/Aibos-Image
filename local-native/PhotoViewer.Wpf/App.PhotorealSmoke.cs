using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class App
{
    private void CaptureModalPhotorealSmoke(string resultPath)
    {
        string resultFullPath = Path.GetFullPath(resultPath);
        string smokeRoot = Directory.CreateTempSubdirectory("aibos-wpf-photoreal-").FullName;
        _ = Dispatcher.BeginInvoke(async () =>
        {
            MainWindow? window = null;
            var previousEnvironment = new Dictionary<string, string?>(StringComparer.Ordinal);
            bool ok = false;
            string failure = "";
            bool selected = false;
            bool opened = false;
            bool passive = false;
            bool started = false;
            bool toolbarContract = false;
            bool requestContract = false;
            bool sharedQueueRoute = false;
            bool versionCycleContract = false;
            bool sourceUntouched = false;
            var requests = new List<string>();
            string createBody = "";
            try
            {
                string imageRoot = Path.Combine(smokeRoot, "images");
                string storesRoot = Path.Combine(smokeRoot, "stores");
                string sourcePath = Path.Combine(imageRoot, "source.png");
                Directory.CreateDirectory(imageRoot);
                Directory.CreateDirectory(storesRoot);
                WritePhotorealSmokePng(sourcePath);
                string sourceHashBefore = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath)));
                var sourceInfo = new FileInfo(sourcePath);
                double sourceMtimeMs = new DateTimeOffset(sourceInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                string enhancementRoot = Path.Combine(storesRoot, "enhance");
                string outputsRoot = Path.Combine(enhancementRoot, "outputs");
                string upscaleOutputPath = Path.Combine(outputsRoot, "upscale.png");
                string photorealOutputPath = Path.Combine(outputsRoot, "photoreal.png");
                Directory.CreateDirectory(outputsRoot);
                File.Copy(sourcePath, upscaleOutputPath);
                File.Copy(sourcePath, photorealOutputPath);
                var upscaleJob = new
                {
                    id = "upscale-version",
                    operation = "upscale",
                    sourceId = sourcePath,
                    sourcePath,
                    sourceSignature = new { size = sourceInfo.Length, mtimeMs = sourceMtimeMs },
                    adapterId = "realesrgan-ncnn",
                    status = "succeeded",
                    progress = 100,
                    outputPath = upscaleOutputPath,
                };
                var photorealJob = new
                {
                    id = "photoreal-version",
                    operation = "photoreal",
                    sourceId = sourcePath,
                    sourcePath,
                    sourceSignature = new { size = sourceInfo.Length, mtimeMs = sourceMtimeMs },
                    adapterId = "comfyui-flux2-photoreal",
                    status = "succeeded",
                    progress = 100,
                    outputPath = photorealOutputPath,
                };
                string jobsPath = Path.Combine(enhancementRoot, "jobs.json");
                File.WriteAllText(
                    jobsPath,
                    JsonSerializer.Serialize(new
                    {
                        version = 1,
                        jobs = new[] { upscaleJob, photorealJob },
                    }));

                var environment = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["PHOTOVIEWER_WPF_STATE_PATH"] = Path.Combine(storesRoot, "state.json"),
                    ["PHOTOVIEWER_WPF_FAVORITES_PATH"] = Path.Combine(storesRoot, "favorites.json"),
                    ["PHOTOVIEWER_WPF_SEEN_PATH"] = Path.Combine(storesRoot, "seen.json"),
                    ["PHOTOVIEWER_WPF_RECENT_PATH"] = Path.Combine(storesRoot, "recent-folders.json"),
                    ["PHOTOVIEWER_WPF_SETTINGS_PATH"] = Path.Combine(storesRoot, "settings.json"),
                    ["PHOTOVIEWER_WPF_ALBUMS_PATH"] = Path.Combine(storesRoot, "albums.json"),
                    ["PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH"] = Path.Combine(storesRoot, "search-history.json"),
                    ["PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"] = jobsPath,
                    ["PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY"] = Path.Combine(storesRoot, "metadata-index"),
                };
                foreach ((string name, string value) in environment)
                {
                    previousEnvironment[name] = Environment.GetEnvironmentVariable(name);
                    Environment.SetEnvironmentVariable(name, value);
                }

                static HttpResponseMessage JsonResponse(HttpStatusCode status, object payload)
                    => new(status)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
                    };

                window = new MainWindow();
                window.SuppressStatePersistence();
                window.ConfigureModalEnhancementForSmoke(async (request, token) =>
                {
                    string route = request.RequestUri?.AbsolutePath ?? "";
                    requests.Add($"{request.Method.Method} {route}");
                    if (request.Method == HttpMethod.Get)
                        return JsonResponse(
                            HttpStatusCode.OK,
                            new { jobs = new[] { photorealJob, upscaleJob } });
                    if (request.Method == HttpMethod.Post
                        && route.EndsWith("/api/enhance/jobs", StringComparison.Ordinal))
                    {
                        createBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
                        return JsonResponse(HttpStatusCode.Accepted, new
                        {
                            job = new
                            {
                                id = "photoreal-smoke-job",
                                operation = "photoreal",
                                sourceId = sourcePath,
                                sourcePath,
                                sourceSignature = new { size = sourceInfo.Length, mtimeMs = sourceMtimeMs },
                                adapterId = "comfyui-flux2-photoreal",
                                status = "queued",
                                progress = 0,
                            },
                        });
                    }
                    return JsonResponse(HttpStatusCode.NotFound, new { error = "unexpected smoke route" });
                });
                window.ConfigureModalPhotorealSettingsForSmoke(0.55, 0.8, 8, 1280);
                window.Show();
                await window.LoadFolderSetAsync([imageRoot], commitRecent: false);
                selected = window.SelectFileNameForSmoke(Path.GetFileName(sourcePath));
                opened = window.OpenModalForSmoke();
                toolbarContract = window.ModalPhotorealToolbarContractForSmoke;
                bool initialPhotoreal = string.Equals(
                    window.ModalDisplayPathForSmoke,
                    photorealOutputPath,
                    StringComparison.OrdinalIgnoreCase);
                bool downToUpscale = window.InvokePreviewKeyForSmoke(Key.Down, ModifierKeys.Control)
                    && string.Equals(
                        window.ModalDisplayPathForSmoke,
                        upscaleOutputPath,
                        StringComparison.OrdinalIgnoreCase);
                bool downToOriginal = window.InvokePreviewKeyForSmoke(Key.Down, ModifierKeys.Control)
                    && string.Equals(
                        window.ModalDisplayPathForSmoke,
                        sourcePath,
                        StringComparison.OrdinalIgnoreCase);
                bool upWrapsToUpscale = window.InvokePreviewKeyForSmoke(Key.Up, ModifierKeys.Control)
                    && string.Equals(
                        window.ModalDisplayPathForSmoke,
                        upscaleOutputPath,
                        StringComparison.OrdinalIgnoreCase);
                versionCycleContract = initialPhotoreal
                    && downToUpscale
                    && downToOriginal
                    && upWrapsToUpscale;
                passive = requests.All(static request => request.StartsWith("GET ", StringComparison.Ordinal));
                started = await window.StartModalPhotorealForSmokeAsync();

                using JsonDocument document = JsonDocument.Parse(createBody);
                JsonElement body = document.RootElement;
                requestContract = body.GetProperty("operation").GetString() == "photoreal"
                    && body.GetProperty("presetId").GetString() == "photoreal-balanced"
                    && body.GetProperty("adapterId").GetString() == "comfyui-flux2-photoreal"
                    && Math.Abs(body.GetProperty("strength").GetDouble() - 0.55) < 0.001
                    && Math.Abs(body.GetProperty("structureStrength").GetDouble() - 0.8) < 0.001
                    && body.GetProperty("steps").GetInt32() == 8
                    && body.GetProperty("maxDimension").GetInt32() == 1280;
                sharedQueueRoute = requests.Any(static request => request == "POST /api/enhance/jobs")
                    && requests.All(static request => !request.Contains("/photoreal/", StringComparison.Ordinal));
                sourceUntouched = sourceHashBefore == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath)));
                ok = selected
                    && opened
                    && passive
                    && started
                    && toolbarContract
                    && versionCycleContract
                    && requestContract
                    && sharedQueueRoute
                    && sourceUntouched
                    && window.ModalEnhancementOperationForSmoke == "photoreal";
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }
            finally
            {
                if (window is not null)
                {
                    try { window.Close(); } catch { }
                }
                foreach ((string name, string? value) in previousEnvironment)
                    Environment.SetEnvironmentVariable(name, value);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(resultFullPath)!);
            File.WriteAllText(
                resultFullPath,
                JsonSerializer.Serialize(new
                {
                    ok,
                    message = ok ? "AI photoreal button, settings, and shared GPU queue request passed." : failure,
                    selected,
                    opened,
                    passive,
                    started,
                    toolbarContract,
                    versionCycleContract,
                    requestContract,
                    sharedQueueRoute,
                    sourceUntouched,
                    requests,
                }, new JsonSerializerOptions { WriteIndented = true }));
            try { Directory.Delete(smokeRoot, recursive: true); } catch { }
            Shutdown(ok ? 0 : 1);
        }, DispatcherPriority.ContextIdle);
    }

    private static void WritePhotorealSmokePng(string path)
    {
        const int width = 16;
        const int height = 24;
        byte[] pixels = new byte[width * height * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 0x70;
            pixels[index + 1] = 0x90;
            pixels[index + 2] = 0xB0;
            pixels[index + 3] = 0xFF;
        }
        BitmapSource bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}

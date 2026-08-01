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
            bool resetPromptContract = false;
            bool appSettingsPromptContract = false;
            bool appSettingsControlsContract = false;
            bool styleContract = false;
            bool stylePersistenceContract = false;
            bool styleReloadContract = false;
            bool defaultPromptContract = false;
            bool veryHighQualityContract = false;
            bool sharedQueueRoute = false;
            bool versionCycleContract = false;
            bool sourceUntouched = false;
            bool independentCompanionContract = false;
            bool galleryContextNoModal = false;
            bool galleryContextDirectContract = false;
            bool modalPhotorealOperation = false;
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
                string upscaleOutputPath = Path.Combine(outputsRoot, "upscale", "upscale.png");
                string photorealOutputPath = Path.Combine(outputsRoot, "photoreal", "photoreal.png");
                Directory.CreateDirectory(Path.GetDirectoryName(upscaleOutputPath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(photorealOutputPath)!);
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
                const string customPrompt = "custom adult photoreal portrait";
                window.ConfigureModalPhotorealSettingsForSmoke(0.55, 0.8, 12, 1280, customPrompt);
                veryHighQualityContract = window.ModalPhotorealSettingsForSmoke.Steps == 12;
                window.ConfigureModalPhotorealSettingsForSmoke(0.55, 0.8, 8, 1280, customPrompt);
                bool customPromptApplied = window.ModalPhotorealSettingsForSmoke.Prompt == customPrompt;
                const string styleName = "Soft Japanese portrait";
                const string stylePrompt = "adult Japanese portrait preserving the source expression";
                window.ConfigureModalPhotorealSettingsForSmoke(0.4, 1.1, 6, 1024, stylePrompt, 1.35);
                bool styleSaved = window.SavePhotorealStyleForSmoke(styleName);
                window.FlushStateForSmoke();
                ViewerState? persistedStyleState = JsonSerializer.Deserialize<ViewerState>(
                    File.ReadAllText(environment["PHOTOVIEWER_WPF_STATE_PATH"]));
                PhotorealStyleState? persistedStyle =
                    persistedStyleState?.PhotorealStyles?.SingleOrDefault();
                stylePersistenceContract = persistedStyle is not null
                    && persistedStyle.Name == styleName
                    && Math.Abs(persistedStyle.Strength - 0.4) < 0.001
                    && Math.Abs(persistedStyle.StructureStrength - 1.1) < 0.001
                    && Math.Abs((persistedStyle.CfgScale ?? 0) - 1.35) < 0.001
                    && persistedStyle.Steps == 6
                    && persistedStyle.MaxDimension == 1024
                    && persistedStyle.Prompt == stylePrompt
                    && persistedStyleState?.SelectedPhotorealStyleName == styleName;
                var reloadedStyleWindow = new MainWindow();
                try
                {
                    reloadedStyleWindow.SuppressStatePersistence();
                    var reloadedStyleSettings = reloadedStyleWindow.ModalPhotorealSettingsForSmoke;
                    styleReloadContract = reloadedStyleWindow.PhotorealStyleNamesForSmoke.Contains(
                            styleName,
                            StringComparer.OrdinalIgnoreCase)
                        && Math.Abs(reloadedStyleSettings.Strength - 0.4) < 0.001
                        && Math.Abs(reloadedStyleSettings.StructureStrength - 1.1) < 0.001
                        && Math.Abs(reloadedStyleSettings.CfgScale - 1.35) < 0.001
                        && reloadedStyleSettings.Steps == 6
                        && reloadedStyleSettings.MaxDimension == 1024
                        && reloadedStyleSettings.Prompt == stylePrompt;
                }
                finally
                {
                    reloadedStyleWindow.Close();
                }
                window.ConfigureModalPhotorealSettingsForSmoke(0.7, 0.25, 12, 768, "temporary custom settings");
                bool styleSelected = window.SelectPhotorealStyleForSmoke(styleName);
                var appliedStyle = window.ModalPhotorealSettingsForSmoke;
                bool styleApplied = Math.Abs(appliedStyle.Strength - 0.4) < 0.001
                    && Math.Abs(appliedStyle.StructureStrength - 1.1) < 0.001
                    && Math.Abs(appliedStyle.CfgScale - 1.35) < 0.001
                    && appliedStyle.Steps == 6
                    && appliedStyle.MaxDimension == 1024
                    && appliedStyle.Prompt == stylePrompt;
                bool styleDeleted = window.DeleteSelectedPhotorealStyleForSmoke();
                styleContract = window.PhotorealStyleSurfaceForSmoke
                    && styleSaved
                    && styleSelected
                    && styleApplied
                    && styleDeleted
                    && !window.PhotorealStyleNamesForSmoke.Contains(styleName, StringComparer.OrdinalIgnoreCase);
                const string appSettingsPrompt = "adult Japanese portrait with unchanged expression";
                window.SetAppPhotorealPromptForSmoke(appSettingsPrompt);
                bool appToModalSynchronized =
                    window.ModalPhotorealSettingsForSmoke.Prompt == appSettingsPrompt;
                window.ResetAppPhotorealPromptForSmoke();
                appSettingsPromptContract = window.AppPhotorealPromptSurfaceForSmoke
                    && appToModalSynchronized
                    && window.ModalPhotorealSettingsForSmoke.Prompt == window.DefaultModalPhotorealPromptForSmoke;
                window.ConfigureModalPhotorealSettingsForSmoke(
                    0.3,
                    0.4,
                    12,
                    768,
                    "temporary reset contract",
                    1.75);
                window.ResetAppPhotorealSettingsForSmoke();
                var resetSettings = window.ModalPhotorealSettingsForSmoke;
                appSettingsControlsContract = window.AppPhotorealSettingsSurfaceForSmoke
                    && Math.Abs(resetSettings.Strength - 0.8) < 0.001
                    && Math.Abs(resetSettings.StructureStrength - 1.0) < 0.001
                    && Math.Abs(resetSettings.CfgScale - 1.0) < 0.001
                    && resetSettings.Steps == 8
                    && resetSettings.MaxDimension == 1280
                    && resetSettings.Prompt == window.DefaultModalPhotorealPromptForSmoke;
                defaultPromptContract = window.DefaultModalPhotorealPromptForSmoke.Contains(
                        "five natural fingers",
                        StringComparison.Ordinal)
                    && window.DefaultModalPhotorealPromptForSmoke.Contains(
                        "exact expression and emotion",
                        StringComparison.Ordinal)
                    && window.DefaultModalPhotorealPromptForSmoke.Contains(
                        "Do not add a smile unless the source is smiling",
                        StringComparison.Ordinal)
                    && !window.DefaultModalPhotorealPromptForSmoke.Contains(
                        "ADetailer",
                        StringComparison.OrdinalIgnoreCase);
                EnhancementCompanionLaunchContractSmokeSnapshot companionLaunch =
                    PhotoViewer.Wpf.MainWindow.EnhancementCompanionLaunchContractForSmoke();
                independentCompanionContract = !companionLaunch.UseShellExecute
                    && companionLaunch.CreateNoWindow
                    && !companionLaunch.RedirectStandardOutput
                    && !companionLaunch.RedirectStandardError
                    && !companionLaunch.HasExplicitWorkingDirectory
                    && !companionLaunch.HasExternalOwnerPid
                    && companionLaunch.NoOpen == "1"
                    && companionLaunch.ComfyAutostart == "0";
                window.ConfigureModalPhotorealSettingsForSmoke(0.55, 0.8, 8, 1280, customPrompt, 1.25);
                window.ResetModalPhotorealPromptForSmoke();
                resetPromptContract = customPromptApplied
                    && window.ModalPhotorealSettingsForSmoke.Prompt == window.DefaultModalPhotorealPromptForSmoke
                    && window.AppPhotorealPromptSurfaceForSmoke;
                window.ConfigureModalPhotorealSettingsForSmoke(0.55, 0.8, 8, 1280, customPrompt, 1.25);
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
                modalPhotorealOperation =
                    window.ModalEnhancementOperationForSmoke == "photoreal";
                int createRequestsBeforeGalleryContext = requests.Count(static request =>
                    request == "POST /api/enhance/jobs");
                window.CloseModalForSmoke();
                galleryContextNoModal = await window.StartGalleryContextEnhancementForSmokeAsync("photoreal");
                galleryContextDirectContract = galleryContextNoModal
                    && requests.Count(static request => request == "POST /api/enhance/jobs")
                        == createRequestsBeforeGalleryContext + 1;

                using JsonDocument document = JsonDocument.Parse(createBody);
                JsonElement body = document.RootElement;
                requestContract = body.GetProperty("operation").GetString() == "photoreal"
                    && body.GetProperty("presetId").GetString() == "photoreal-balanced"
                    && body.GetProperty("adapterId").GetString() == "comfyui-flux2-photoreal"
                    && Math.Abs(body.GetProperty("strength").GetDouble() - 0.55) < 0.001
                    && Math.Abs(body.GetProperty("structureStrength").GetDouble() - 0.8) < 0.001
                    && Math.Abs(body.GetProperty("cfgScale").GetDouble() - 1.25) < 0.001
                    && body.GetProperty("steps").GetInt32() == 8
                    && body.GetProperty("maxDimension").GetInt32() == 1280
                    && body.GetProperty("prompt").GetString() == customPrompt;
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
                    && resetPromptContract
                    && appSettingsPromptContract
                    && appSettingsControlsContract
                    && styleContract
                    && stylePersistenceContract
                    && styleReloadContract
                    && defaultPromptContract
                    && veryHighQualityContract
                    && independentCompanionContract
                    && galleryContextDirectContract
                    && sharedQueueRoute
                    && sourceUntouched
                    && modalPhotorealOperation;
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
                    resetPromptContract,
                    appSettingsPromptContract,
                    appSettingsControlsContract,
                    styleContract,
                    stylePersistenceContract,
                    styleReloadContract,
                    defaultPromptContract,
                    veryHighQualityContract,
                    independentCompanionContract,
                    galleryContextNoModal,
                    galleryContextDirectContract,
                    modalPhotorealOperation,
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

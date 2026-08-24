using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace PhotoViewer.Wpf;

public partial class App
{
    private void CaptureVideoDragInSmoke(
        string resultPath,
        IReadOnlyList<string> args)
    {
        string resultFullPath = Path.GetFullPath(resultPath);
        string smokeRoot = Directory.CreateTempSubdirectory(
                "aibos-wpf-video-drag-in-")
            .FullName;
        string catalogFolder = Path.Combine(smokeRoot, "catalog");
        string externalFolder = Path.Combine(smokeRoot, "external");
        string storeRoot = Path.Combine(smokeRoot, "stores");
        string catalogImage = Path.Combine(catalogFolder, "catalog.png");
        string externalVideo = Path.Combine(externalFolder, "external.mp4");
        string secondVideo = Path.Combine(externalFolder, "second.mp4");
        string malformedVideo = Path.Combine(externalFolder, "malformed.mp4");
        string unsupportedFile = Path.Combine(externalFolder, "unsupported.txt");

        var environment = new Dictionary<string, string?>
        {
            ["PHOTOVIEWER_WPF_STATE_PATH"] = Path.Combine(storeRoot, "state.json"),
            ["PHOTOVIEWER_WPF_FAVORITES_PATH"] = Path.Combine(storeRoot, "favorites.json"),
            ["PHOTOVIEWER_WPF_SEEN_PATH"] = Path.Combine(storeRoot, "seen.json"),
            ["PHOTOVIEWER_WPF_RECENT_PATH"] = Path.Combine(storeRoot, "recent-folders.json"),
            ["PHOTOVIEWER_WPF_SETTINGS_PATH"] = Path.Combine(storeRoot, "settings.json"),
            ["PHOTOVIEWER_WPF_ALBUMS_PATH"] = Path.Combine(storeRoot, "albums.json"),
            ["PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH"] = Path.Combine(storeRoot, "search-history.json"),
            ["PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY"] = Path.Combine(storeRoot, "metadata-index"),
            ["PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"] = Path.Combine(storeRoot, "enhance", "jobs.json"),
            ["PHOTOVIEWER_WPF_ENHANCEMENT_OUTPUT_ROOT"] = Path.Combine(storeRoot, "outputs"),
        };
        var previousEnvironment = environment.Keys.ToDictionary(
            static key => key,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);

        object result = new { ok = false, message = "Smoke did not complete." };
        bool ok = false;
        MainWindow? window = null;
        try
        {
            Directory.CreateDirectory(catalogFolder);
            Directory.CreateDirectory(externalFolder);
            Directory.CreateDirectory(Path.GetDirectoryName(
                environment["PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"]!)!);
            WriteSmokePng(catalogImage, 48, 32, System.Windows.Media.Colors.SteelBlue);
            string? mediaFixtureArgument = ArgValue(
                args.ToArray(),
                "--media-fixture-base64");
            bool useRealMediaTransport =
                !string.IsNullOrWhiteSpace(mediaFixtureArgument);
            if (useRealMediaTransport)
            {
                string fixtureText = File.ReadAllText(
                    Path.GetFullPath(mediaFixtureArgument!),
                    System.Text.Encoding.ASCII);
                byte[] fixtureBytes = Convert.FromBase64String(
                    string.Concat(fixtureText.Where(
                        static character => !char.IsWhiteSpace(character))));
                File.WriteAllBytes(externalVideo, fixtureBytes);
                File.WriteAllBytes(secondVideo, fixtureBytes);
            }
            else
            {
                WriteIsoBmffSmokeVideo(externalVideo);
                WriteIsoBmffSmokeVideo(secondVideo);
            }
            File.WriteAllText(malformedVideo, "not an ISO BMFF video");
            File.WriteAllText(unsupportedFile, "unsupported");

            foreach ((string key, string? value) in environment)
                Environment.SetEnvironmentVariable(key, value);

            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            window = HiddenWindow();
            if (!useRealMediaTransport)
                window.EnableModalVideoTransportStubForSmoke();
            int companionCalls = 0;
            window.ConfigureModalEnhancementForSmoke((_, _) =>
            {
                Interlocked.Increment(ref companionCalls);
                return Task.FromResult(new HttpResponseMessage(
                    HttpStatusCode.ServiceUnavailable));
            });
            window.Show();
            window.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await window.LoadFolderAsync(catalogFolder);
                    _ = window.SelectFileNameForSmoke(Path.GetFileName(catalogImage));
                    _ = await window.DrainSharedStoreWritersForSmokeAsync();
                    string[] durableStorePaths = environment.Values
                        .Where(static path => !string.IsNullOrWhiteSpace(path))
                        .Select(static path => path!)
                        .Where(path => !string.Equals(
                            path,
                            environment["PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY"],
                            StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(
                                path,
                                environment["PHOTOVIEWER_WPF_ENHANCEMENT_OUTPUT_ROOT"],
                                StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    string StoreSetFingerprint()
                        => string.Join(
                            "|",
                            durableStorePaths.Select(path =>
                                $"{path.Length}:{FileFingerprint(path)}"));
                    string storesBefore = StoreSetFingerprint();
                    string sourceBefore = FileFingerprint(externalVideo);

                    ViewerDropClassificationSmokeSnapshot acceptedClass =
                        window.ClassifyViewerDropForSmoke([externalVideo]);
                    ViewerDropClassificationSmokeSnapshot multipleClass =
                        window.ClassifyViewerDropForSmoke(
                            [externalVideo, secondVideo]);
                    ViewerDropClassificationSmokeSnapshot mixedClass =
                        window.ClassifyViewerDropForSmoke(
                            [externalVideo, catalogImage]);
                    ViewerDropClassificationSmokeSnapshot unsupportedClass =
                        window.ClassifyViewerDropForSmoke([unsupportedFile]);
                    ViewerDropClassificationSmokeSnapshot folderClass =
                        window.ClassifyViewerDropForSmoke([externalFolder]);
                    ExternalVideoDropSmokeSnapshot malformed =
                        await window.DropExternalVideoForSmokeAsync(
                            [malformedVideo]);
                    ExternalVideoDropSmokeSnapshot multiple =
                        await window.DropExternalVideoForSmokeAsync(
                            [externalVideo, secondVideo]);
                    ExternalVideoDropSmokeSnapshot directory =
                        await window.DropExternalVideoForSmokeAsync(
                            [externalFolder]);
                    ExternalVideoDropSmokeSnapshot accepted =
                        await window.DropExternalVideoForSmokeAsync(
                            [externalVideo]);
                    bool mediaOpened = !useRealMediaTransport
                        || await window.WaitForModalVideoMediaOpenedForSmokeAsync();
                    bool realTransportReady = !useRealMediaTransport
                        || mediaOpened
                            && window.ModalVideoHasNaturalDurationForSmoke;
                    ExternalVideoSourceSeamSmokeSnapshot? sourceSeam =
                        window.CaptureExternalVideoSourceSeamForSmoke();
                    window.BeginModalEnhancementForSmoke();
                    await Task.Delay(100);
                    bool explicitAiStillBlocked = companionCalls == 0
                        && window.ExternalVideoDropSessionActiveForSmoke;
                    _ = await window.DrainSharedStoreWritersForSmokeAsync();
                    bool dropStoresUnchanged = string.Equals(
                        storesBefore,
                        StoreSetFingerprint(),
                        StringComparison.Ordinal);

                    bool writeBlockedWhilePinned;
                    try
                    {
                        using FileStream _ = new(
                            externalVideo,
                            FileMode.Open,
                            FileAccess.Write,
                            FileShare.ReadWrite | FileShare.Delete);
                        writeBlockedWhilePinned = false;
                    }
                    catch (IOException)
                    {
                        writeBlockedWhilePinned = true;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        writeBlockedWhilePinned = true;
                    }

                    _ = window.SelectFileNameForSmoke(Path.GetFileName(catalogImage));
                    bool modalPinnedAcrossBackgroundSelection =
                        window.ExternalVideoDropSessionActiveForSmoke
                        && string.Equals(
                            window.ModalSourcePathForSmoke,
                            Path.GetFullPath(externalVideo),
                            StringComparison.OrdinalIgnoreCase)
                        && string.Equals(
                            window.ModalVideoPathForSmoke,
                            Path.GetFullPath(externalVideo),
                            StringComparison.OrdinalIgnoreCase);
                    bool sourceSeamReusable = sourceSeam is not null
                        && window.RevalidateExternalVideoSourceSeamForSmoke(
                            sourceSeam);
                    bool surfaceSafe = accepted.Accepted
                        && accepted.ModalVisible
                        && accepted.ShowingVideo
                        && accepted.SourcePinned
                        && accepted.AiActionsDisabled
                        && accepted.MutationActionsDisabled
                        && !window.VideoToolsEntryVisibleForSmoke("retake")
                        && !window.VideoToolsEntryVisibleForSmoke("finish");

                    window.CloseModalForSmoke();
                    bool staleSourceSeamRejected = sourceSeam is not null
                        && !window.RevalidateExternalVideoSourceSeamForSmoke(
                            sourceSeam);
                    bool pinReleasedAfterClose;
                    try
                    {
                        using FileStream _ = new(
                            externalVideo,
                            FileMode.Open,
                            FileAccess.Write,
                            FileShare.ReadWrite | FileShare.Delete);
                        pinReleasedAfterClose = true;
                    }
                    catch
                    {
                        pinReleasedAfterClose = false;
                    }

                    bool passive = companionCalls == 0
                        && dropStoresUnchanged;
                    bool sourceUntouched = string.Equals(
                        sourceBefore,
                        FileFingerprint(externalVideo),
                        StringComparison.Ordinal);
                    bool classification = string.Equals(
                            acceptedClass.Kind,
                            "Video",
                            StringComparison.Ordinal)
                        && acceptedClass.AffordanceAccepted
                        && string.Equals(
                            multipleClass.Kind,
                            "Video",
                            StringComparison.Ordinal)
                        && !multipleClass.AffordanceAccepted
                        && string.Equals(
                            mixedClass.Kind,
                            "Mixed",
                            StringComparison.Ordinal)
                        && !mixedClass.AffordanceAccepted
                        && string.Equals(
                            unsupportedClass.Kind,
                            "Rejected",
                            StringComparison.Ordinal)
                        && !unsupportedClass.AffordanceAccepted
                        && string.Equals(
                            folderClass.Kind,
                            "Folders",
                            StringComparison.Ordinal)
                        && folderClass.AffordanceAccepted;
                    bool mixedRejected = string.Equals(
                            mixedClass.Kind,
                            "Mixed",
                            StringComparison.Ordinal)
                        && !mixedClass.AffordanceAccepted;
                    bool multipleRejected = !multiple.Accepted
                        && !multipleClass.AffordanceAccepted;
                    bool directoryRejected = !directory.Accepted
                        && string.Equals(
                            folderClass.Kind,
                            "Folders",
                            StringComparison.Ordinal)
                        && folderClass.AffordanceAccepted;
                    bool unsupportedRejected = string.Equals(
                            unsupportedClass.Kind,
                            "Rejected",
                            StringComparison.Ordinal)
                        && !unsupportedClass.AffordanceAccepted;
                    bool invalidRejected = !malformed.Accepted
                        && mixedRejected
                        && multipleRejected
                        && directoryRejected
                        && unsupportedRejected;
                    bool singleVideoPass = surfaceSafe
                        && realTransportReady;

                    ok = classification
                        && invalidRejected
                        && surfaceSafe
                        && realTransportReady
                        && explicitAiStillBlocked
                        && writeBlockedWhilePinned
                        && modalPinnedAcrossBackgroundSelection
                        && sourceSeamReusable
                        && staleSourceSeamRejected
                        && pinReleasedAfterClose
                        && !window.ExternalVideoDropSessionActiveForSmoke
                        && passive
                        && sourceUntouched;
                    result = new
                    {
                        ok,
                        classification,
                        singleVideoPass,
                        invalidRejected,
                        mixedRejected,
                        multipleRejected,
                        directoryRejected,
                        unsupportedRejected,
                        surfaceSafe,
                        useRealMediaTransport,
                        mediaOpened,
                        realTransportReady,
                        explicitAiStillBlocked,
                        writeBlockedWhilePinned,
                        modalPinnedAcrossBackgroundSelection,
                        sourceSeamReusable,
                        staleSourceSeamRejected,
                        pinReleasedAfterClose,
                        passive,
                        dropStoresUnchanged,
                        companionCalls,
                        sourceUntouched,
                    };
                }
                catch (Exception ex)
                {
                    result = new
                    {
                        ok = false,
                        exceptionType = ex.GetType().Name,
                        message = ex.Message,
                        stackTrace = ex.ToString(),
                    };
                }
                finally
                {
                    try { window.Close(); } catch { }
                    foreach ((string key, string? value) in previousEnvironment)
                        Environment.SetEnvironmentVariable(key, value);
                    Directory.CreateDirectory(Path.GetDirectoryName(resultFullPath)!);
                    File.WriteAllText(
                        resultFullPath,
                        JsonSerializer.Serialize(
                            result,
                            new JsonSerializerOptions { WriteIndented = true }));
                    try
                    {
                        if (Directory.Exists(smokeRoot))
                            Directory.Delete(smokeRoot, recursive: true);
                    }
                    catch
                    {
                    }
                    Shutdown(ok ? 0 : 1);
                }
            });
        }
        catch (Exception ex)
        {
            result = new
            {
                ok = false,
                exceptionType = ex.GetType().Name,
                message = ex.Message,
                stackTrace = ex.ToString(),
            };
            foreach ((string key, string? value) in previousEnvironment)
                Environment.SetEnvironmentVariable(key, value);
            Directory.CreateDirectory(Path.GetDirectoryName(resultFullPath)!);
            File.WriteAllText(
                resultFullPath,
                JsonSerializer.Serialize(
                    result,
                    new JsonSerializerOptions { WriteIndented = true }));
            try
            {
                if (Directory.Exists(smokeRoot))
                    Directory.Delete(smokeRoot, recursive: true);
            }
            catch
            {
            }
            Shutdown(1);
        }
    }

    private static void WriteIsoBmffSmokeVideo(string path)
    {
        byte[] header =
        [
            0, 0, 0, 24,
            (byte)'f', (byte)'t', (byte)'y', (byte)'p',
            (byte)'i', (byte)'s', (byte)'o', (byte)'m',
            0, 0, 0, 0,
            (byte)'i', (byte)'s', (byte)'o', (byte)'m',
            (byte)'m', (byte)'p', (byte)'4', (byte)'2',
        ];
        File.WriteAllBytes(path, header);
    }
}

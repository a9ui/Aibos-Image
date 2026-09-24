using System.IO;
using System.Text.Json;
using System.Windows;

namespace PhotoViewer.Wpf;

public partial class App
{
    private void CaptureDurableEnqueuePublicationSmoke(string resultPath)
    {
        string root = Directory.CreateTempSubdirectory("aibos-enqueue-publication-ui-").FullName;
        var environment = new Dictionary<string, string>
        {
            ["PHOTOVIEWER_WPF_STATE_PATH"] = Path.Combine(root, "state.json"),
            ["PHOTOVIEWER_WPF_FAVORITES_PATH"] = Path.Combine(root, "favorites.json"),
            ["PHOTOVIEWER_WPF_SEEN_PATH"] = Path.Combine(root, "seen.json"),
            ["PHOTOVIEWER_WPF_RECENT_PATH"] = Path.Combine(root, "recent.json"),
            ["PHOTOVIEWER_WPF_SETTINGS_PATH"] = Path.Combine(root, "settings.json"),
            ["PHOTOVIEWER_WPF_ALBUMS_PATH"] = Path.Combine(root, "albums.json"),
            ["PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH"] = Path.Combine(root, "search.json"),
            ["PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY"] = Path.Combine(root, "metadata-index"),
            ["PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"] = Path.Combine(root, "enhance", "jobs.json"),
        };
        var previous = environment.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            MainWindow? window = null;
            object result;
            bool ok = false;
            try
            {
                foreach ((string key, string value) in environment) Environment.SetEnvironmentVariable(key, value);
                Directory.CreateDirectory(Path.Combine(root, "enhance"));
                File.WriteAllText(environment["PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"], "{\"version\":1,\"jobs\":[]}");
                window = HiddenWindow();
                window.SuppressStatePersistence();
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.Show();
                var checks = await window.DurableEnqueuePublicationForSmokeAsync();
                ok = checks.Values.All(value => value);
                result = new { ok, checks, fixtureRoot = root };
            }
            catch (Exception error) { result = new { ok = false, error = error.ToString(), fixtureRoot = root }; }
            finally
            {
                window?.Close();
                foreach ((string key, string? value) in previous) Environment.SetEnvironmentVariable(key, value);
            }
            string fullResultPath = Path.GetFullPath(resultPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullResultPath)!);
            File.WriteAllText(fullResultPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(ok ? 0 : 1);
        });
    }
}

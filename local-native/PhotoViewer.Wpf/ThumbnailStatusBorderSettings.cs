using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhotoViewer.Wpf;

internal sealed record ThumbnailStatusBorderPreference(bool Enabled, string Color);

[Flags]
internal enum ThumbnailStatusBorderDirtyPreferences
{
    None = 0,
    Favorite = 1,
    Enhanced = 2,
    All = Favorite | Enhanced,
}

internal sealed record ThumbnailStatusBorderSettings(
    ThumbnailStatusBorderPreference Favorite,
    ThumbnailStatusBorderPreference Enhanced)
{
    public const string DefaultFavoriteColor = "#facc15";
    public const string RainbowColor = "rainbow";
    public const string DefaultEnhancedColor = "#38bdf8";

    public static ThumbnailStatusBorderSettings Default { get; } = new(
        new ThumbnailStatusBorderPreference(true, DefaultFavoriteColor),
        new ThumbnailStatusBorderPreference(true, DefaultEnhancedColor));
}

internal readonly record struct ThumbnailStatusBorderLoadResult(
    ThumbnailStatusBorderSettings Settings,
    bool IsProtected,
    string? Error);

internal static class ThumbnailStatusBorderSettingsStore
{
    private static readonly string[] BrowserKeyBindingNames =
    [
        "nextImage",
        "prevImage",
        "toggleFavorite",
        "decreaseFavorite",
        "deleteImage",
        "closeModal",
        "flipHorizontal",
        "enhanceImage",
        "toggleFilmstrip",
        "zoomIn",
        "zoomOut",
        "zoomReset",
    ];

    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
    };

    public static ThumbnailStatusBorderLoadResult Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new ThumbnailStatusBorderLoadResult(ThumbnailStatusBorderSettings.Default, false, null);

            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ThumbnailStatusBorderLoadResult(
                ThumbnailStatusBorderSettings.Default,
                true,
                $"Shared settings could not be read: {ex.Message}");
        }
    }

    internal static ThumbnailStatusBorderLoadResult Parse(string json)
    {
        try
        {
            JsonNode? parsed = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            if (parsed is not JsonObject root)
                return Protected("Shared settings JSON must contain an object.");
            if (!TryReadSettings(root, out ThumbnailStatusBorderSettings settings, out string? error))
                return Protected(error ?? "Shared thumbnail border settings do not match the supported schema.");
            return new ThumbnailStatusBorderLoadResult(settings, false, null);
        }
        catch (JsonException ex)
        {
            return Protected($"Shared settings JSON is malformed: {ex.Message}");
        }
    }

    public static bool TryMerge(
        string? existingJson,
        ThumbnailStatusBorderSettings settings,
        ThumbnailStatusBorderDirtyPreferences dirtyPreferences,
        out string mergedJson,
        out string? error)
    {
        mergedJson = "";
        error = null;
        try
        {
            if (dirtyPreferences == ThumbnailStatusBorderDirtyPreferences.None)
            {
                error = "At least one thumbnail border preference must be selected for saving.";
                return false;
            }

            JsonObject root;
            if (string.IsNullOrWhiteSpace(existingJson))
            {
                root = new JsonObject();
            }
            else
            {
                JsonNode? parsed = JsonNode.Parse(existingJson, documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                });
                if (parsed is not JsonObject parsedRoot)
                {
                    error = "Shared settings JSON must contain an object.";
                    return false;
                }
                root = parsedRoot;
                if (!TryReadSettings(root, out _, out error))
                    return false;
            }

            JsonObject borders = GetOrCreateObject(root, "thumbnailStatusBorders");
            if (dirtyPreferences.HasFlag(ThumbnailStatusBorderDirtyPreferences.Favorite))
            {
                JsonObject favorite = GetOrCreateObject(borders, "favorite");
                favorite["enabled"] = settings.Favorite.Enabled;
                favorite["color"] = NormalizeFavoriteColor(settings.Favorite.Color);
            }
            if (dirtyPreferences.HasFlag(ThumbnailStatusBorderDirtyPreferences.Enhanced))
            {
                JsonObject enhanced = GetOrCreateObject(borders, "enhanced");
                enhanced["enabled"] = settings.Enhanced.Enabled;
                enhanced["color"] = NormalizeEnhancedColor(settings.Enhanced.Color);
            }

            mergedJson = root.ToJsonString(IndentedJson) + Environment.NewLine;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Shared settings JSON is malformed: {ex.Message}";
            return false;
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryNormalizeColor(string? value, out string color)
    {
        color = "";
        string candidate = value?.Trim() ?? "";
        if (candidate.Length != 7 || candidate[0] != '#')
            return false;
        for (int index = 1; index < candidate.Length; index++)
        {
            char current = candidate[index];
            bool isHex = current is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F';
            if (!isHex)
                return false;
        }
        color = candidate.ToLowerInvariant();
        return true;
    }

    public static bool TryNormalizeEnhancedColor(string? value, out string color)
    {
        string candidate = value?.Trim() ?? "";
        if (string.Equals(candidate, ThumbnailStatusBorderSettings.RainbowColor, StringComparison.OrdinalIgnoreCase))
        {
            // Read old shared files without rejecting them, but never revive
            // the removed rainbow presentation.
            color = ThumbnailStatusBorderSettings.DefaultEnhancedColor;
            return true;
        }
        return TryNormalizeColor(candidate, out color);
    }

    private static bool TryReadSettings(
        JsonObject root,
        out ThumbnailStatusBorderSettings settings,
        out string? error)
    {
        settings = ThumbnailStatusBorderSettings.Default;
        error = null;
        if (root.TryGetPropertyValue("confirmBeforeDelete", out JsonNode? confirmNode)
            && (confirmNode is not JsonValue confirmValue || !confirmValue.TryGetValue(out bool _)))
        {
            error = "confirmBeforeDelete must be a boolean.";
            return false;
        }
        if (root.TryGetPropertyValue("keyBindings", out JsonNode? bindingsNode))
        {
            if (bindingsNode is not JsonObject bindings)
            {
                error = "keyBindings must contain an object.";
                return false;
            }
            foreach (string name in BrowserKeyBindingNames)
            {
                if (!bindings.TryGetPropertyValue(name, out JsonNode? bindingNode))
                    continue;
                if (bindingNode is not JsonValue bindingValue
                    || !bindingValue.TryGetValue(out string? binding)
                    || string.IsNullOrEmpty(binding)
                    || binding.Length > 64)
                {
                    error = $"keyBindings.{name} must be a non-empty bounded string.";
                    return false;
                }
            }
        }
        if (!root.TryGetPropertyValue("thumbnailStatusBorders", out JsonNode? bordersNode))
            return true;
        if (bordersNode is not JsonObject borders)
        {
            error = "thumbnailStatusBorders must contain an object.";
            return false;
        }

        if (!TryReadPreference(
                borders,
                "favorite",
                settings.Favorite,
                allowRainbow: false,
                out ThumbnailStatusBorderPreference favorite,
                out error)
            || !TryReadPreference(
                borders,
                "enhanced",
                settings.Enhanced,
                allowRainbow: true,
                out ThumbnailStatusBorderPreference enhanced,
                out error))
        {
            return false;
        }

        settings = new ThumbnailStatusBorderSettings(favorite, enhanced);
        return true;
    }

    private static bool TryReadPreference(
        JsonObject borders,
        string name,
        ThumbnailStatusBorderPreference fallback,
        bool allowRainbow,
        out ThumbnailStatusBorderPreference preference,
        out string? error)
    {
        preference = fallback;
        error = null;
        if (!borders.TryGetPropertyValue(name, out JsonNode? preferenceNode))
            return true;
        if (preferenceNode is not JsonObject preferenceObject)
        {
            error = $"thumbnailStatusBorders.{name} must contain an object.";
            return false;
        }

        bool enabled = fallback.Enabled;
        string color = fallback.Color;
        if (preferenceObject.TryGetPropertyValue("enabled", out JsonNode? enabledNode))
        {
            if (enabledNode is not JsonValue enabledValue || !enabledValue.TryGetValue(out enabled))
            {
                error = $"thumbnailStatusBorders.{name}.enabled must be a boolean.";
                return false;
            }
        }
        if (preferenceObject.TryGetPropertyValue("color", out JsonNode? colorNode))
        {
            if (colorNode is not JsonValue colorValue
                || !colorValue.TryGetValue(out string? rawColor)
                || !(allowRainbow
                    ? TryNormalizeEnhancedColor(rawColor, out color)
                    : TryNormalizeColor(rawColor, out color)))
            {
                error = allowRainbow
                    ? $"thumbnailStatusBorders.{name}.color must be rainbow or a six-digit hex color."
                    : $"thumbnailStatusBorders.{name}.color must be a six-digit hex color.";
                return false;
            }
        }

        preference = new ThumbnailStatusBorderPreference(enabled, color);
        return true;
    }

    private static JsonObject GetOrCreateObject(JsonObject parent, string name)
    {
        if (!parent.TryGetPropertyValue(name, out JsonNode? node))
        {
            var created = new JsonObject();
            parent[name] = created;
            return created;
        }
        if (node is JsonObject existing)
            return existing;
        throw new InvalidOperationException($"{name} must contain an object.");
    }

    private static string NormalizeFavoriteColor(string value)
        => TryNormalizeColor(value, out string normalized)
            ? normalized
            : throw new InvalidOperationException("Favorite thumbnail border color must be a six-digit hex value.");

    private static string NormalizeEnhancedColor(string value)
        => TryNormalizeEnhancedColor(value, out string normalized)
            ? normalized
            : throw new InvalidOperationException("AI-enhanced thumbnail border color must be rainbow or a six-digit hex value.");

    private static ThumbnailStatusBorderLoadResult Protected(string error)
        => new(ThumbnailStatusBorderSettings.Default, true, error);
}

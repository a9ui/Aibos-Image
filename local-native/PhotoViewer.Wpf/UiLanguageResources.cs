using System.Globalization;
using System.Windows;

namespace PhotoViewer.Wpf;

internal static class UiLanguageResources
{
    internal const string English = "en";
    internal const string Japanese = "ja";

    private const string DictionaryPrefix = "Localization/StringResources.";
    private static ResourceDictionary? _activeDictionary;

    internal static string CurrentLanguage { get; private set; } = English;

    internal static string Normalize(string? language)
        => string.Equals(language, Japanese, StringComparison.OrdinalIgnoreCase)
            ? Japanese
            : English;

    internal static void Apply(string? language)
    {
        Application app = Application.Current
            ?? throw new InvalidOperationException("The application resource dictionary is unavailable.");
        string normalized = Normalize(language);
        var replacement = new ResourceDictionary
        {
            Source = new Uri(
                $"{DictionaryPrefix}{normalized}.xaml",
                UriKind.Relative),
        };

        var dictionaries = app.Resources.MergedDictionaries;
        for (int index = dictionaries.Count - 1; index >= 0; index--)
        {
            ResourceDictionary candidate = dictionaries[index];
            if (ReferenceEquals(candidate, _activeDictionary)
                || candidate.Source?.OriginalString.StartsWith(
                    DictionaryPrefix,
                    StringComparison.OrdinalIgnoreCase) == true)
            {
                dictionaries.RemoveAt(index);
            }
        }

        dictionaries.Add(replacement);
        _activeDictionary = replacement;
        CurrentLanguage = normalized;
    }

    internal static string Text(string key)
        => Application.Current?.TryFindResource(key) as string ?? key;

    internal static string Format(string key, params object[] arguments)
        => string.Format(CultureInfo.CurrentCulture, Text(key), arguments);
}

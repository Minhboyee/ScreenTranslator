namespace Translator.Windows;

public static class OcrLanguageCatalog
{
    private static readonly IReadOnlyDictionary<string, string> mappings =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ja"] = "ja-JP",
            ["ja-jp"] = "ja-JP",
            ["zh-hans"] = "zh-Hans",
            ["zh-cn"] = "zh-Hans",
            ["zh-sg"] = "zh-Hans",
            ["zh-hant"] = "zh-Hant",
            ["zh-tw"] = "zh-Hant",
            ["zh-hk"] = "zh-Hant"
        };

    public static string Map(string sourceLanguage)
    {
        ArgumentNullException.ThrowIfNull(sourceLanguage);
        var normalized = sourceLanguage.Trim().Replace('_', '-').ToLowerInvariant();
        if (mappings.TryGetValue(normalized, out var languageTag))
        {
            return languageTag;
        }

        throw new ArgumentException(
            "OCR supports ja-JP, zh-Hans, and zh-Hant languages.",
            nameof(sourceLanguage));
    }

    public static bool TryMap(string sourceLanguage, out string languageTag)
    {
        if (sourceLanguage is null)
        {
            languageTag = string.Empty;
            return false;
        }

        var normalized = sourceLanguage.Trim().Replace('_', '-').ToLowerInvariant();
        return mappings.TryGetValue(normalized, out languageTag!);
    }
}

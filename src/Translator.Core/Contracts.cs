using System.Collections.ObjectModel;
using System.Text;

namespace Translator.Core;

public static class TextNormalization
{
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var normalized = value.Normalize();
        var builder = new StringBuilder(normalized.Length);
        var pendingWhitespace = false;

        foreach (var character in normalized)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingWhitespace = builder.Length > 0;
                continue;
            }

            if (pendingWhitespace)
            {
                builder.Append(' ');
                pendingWhitespace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}

public sealed record SourceText
{
    public SourceText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Text must contain a non-whitespace value.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public string NormalizedValue => TextNormalization.Normalize(Value);
}

public readonly record struct PhysicalPixelRect
{
    public PhysicalPixelRect(int left, int top, int width, int height)
    {
        if (width < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public int Left { get; }

    public int Top { get; }

    public int Width { get; }

    public int Height { get; }

    public int Right => checked(Left + Width);

    public int Bottom => checked(Top + Height);
}

public enum OverlayForegroundTone
{
    Dark,
    Light
}

public readonly record struct OcrLineAppearanceHint
{
    public OcrLineAppearanceHint(double relativeBackgroundLuminance)
    {
        if (double.IsNaN(relativeBackgroundLuminance) ||
            double.IsInfinity(relativeBackgroundLuminance) ||
            relativeBackgroundLuminance < 0 ||
            relativeBackgroundLuminance > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(relativeBackgroundLuminance));
        }

        RelativeBackgroundLuminance = relativeBackgroundLuminance;
    }

    public double RelativeBackgroundLuminance { get; }

    public OverlayForegroundTone PreferredForeground =>
        OcrContrastSelector.Select(RelativeBackgroundLuminance);

    public bool PrefersLightForeground => PreferredForeground == OverlayForegroundTone.Light;
}

public static class OcrContrastSelector
{
    public static OverlayForegroundTone Select(OcrLineAppearanceHint appearance)
    {
        return Select(appearance.RelativeBackgroundLuminance);
    }

    public static OverlayForegroundTone Select(double relativeBackgroundLuminance)
    {
        ValidateLuminance(relativeBackgroundLuminance);
        var lightContrast = ContrastRatio(relativeBackgroundLuminance, 1);
        var darkContrast = ContrastRatio(relativeBackgroundLuminance, 0);
        return lightContrast > darkContrast
            ? OverlayForegroundTone.Light
            : OverlayForegroundTone.Dark;
    }

    public static double ContrastRatio(
        double firstRelativeLuminance,
        double secondRelativeLuminance)
    {
        ValidateLuminance(firstRelativeLuminance);
        ValidateLuminance(secondRelativeLuminance);
        var lighter = Math.Max(firstRelativeLuminance, secondRelativeLuminance);
        var darker = Math.Min(firstRelativeLuminance, secondRelativeLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static void ValidateLuminance(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }
}

public sealed record OcrText
{
    public OcrText(
        SourceText text,
        PhysicalPixelRect bounds,
        OcrLineAppearanceHint? appearanceHint = null)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Bounds = bounds;
        AppearanceHint = appearanceHint;
    }

    public OcrText(
        string text,
        PhysicalPixelRect bounds,
        OcrLineAppearanceHint? appearanceHint = null)
        : this(new SourceText(text), bounds, appearanceHint)
    {
    }

    public SourceText Text { get; }

    public PhysicalPixelRect Bounds { get; }

    public OcrLineAppearanceHint? AppearanceHint { get; }

    public OcrLineAppearanceHint? Appearance => AppearanceHint;

    public double? RelativeBackgroundLuminance =>
        AppearanceHint?.RelativeBackgroundLuminance;

    public double? BackgroundLuminance => RelativeBackgroundLuminance;

    public OcrText WithAppearance(OcrLineAppearanceHint? appearanceHint)
    {
        return new OcrText(Text, Bounds, appearanceHint);
    }
}

public sealed record OcrResult
{
    public OcrResult(IEnumerable<OcrText> text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = new ReadOnlyCollection<OcrText>(text.ToArray());
    }

    public IReadOnlyList<OcrText> Text { get; }
}

public sealed record LanguagePair
{
    public LanguagePair(string sourceLanguage, string targetLanguage)
    {
        SourceLanguage = NormalizeLanguage(sourceLanguage, nameof(sourceLanguage));
        TargetLanguage = NormalizeLanguage(targetLanguage, nameof(targetLanguage));
    }

    public string SourceLanguage { get; }

    public string TargetLanguage { get; }

    private static string NormalizeLanguage(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Language must contain a value.", parameterName);
        }

        return normalized;
    }
}

public sealed record TranslationRequest
{
    public TranslationRequest(
        SourceText text,
        LanguagePair languagePair,
        string providerRevision)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        LanguagePair = languagePair ?? throw new ArgumentNullException(nameof(languagePair));

        ArgumentNullException.ThrowIfNull(providerRevision);
        ProviderRevision = providerRevision.Trim();
        if (ProviderRevision.Length == 0)
        {
            throw new ArgumentException("Provider revision must contain a value.", nameof(providerRevision));
        }
    }

    public TranslationRequest(
        string text,
        string sourceLanguage,
        string targetLanguage,
        string providerRevision)
        : this(new SourceText(text), new LanguagePair(sourceLanguage, targetLanguage), providerRevision)
    {
    }

    public SourceText Text { get; }

    public LanguagePair LanguagePair { get; }

    public string ProviderRevision { get; }

    public TranslationMemoryKey MemoryKey => new(Text.NormalizedValue, LanguagePair, ProviderRevision);
}

public readonly record struct TranslationMemoryKey
{
    public TranslationMemoryKey(
        string normalizedText,
        LanguagePair languagePair,
        string providerRevision)
    {
        var text = TextNormalization.Normalize(normalizedText);
        if (text.Length == 0)
        {
            throw new ArgumentException("Normalized text must contain a value.", nameof(normalizedText));
        }

        ArgumentNullException.ThrowIfNull(languagePair);
        ArgumentNullException.ThrowIfNull(providerRevision);
        var revision = providerRevision.Trim();
        if (revision.Length == 0)
        {
            throw new ArgumentException("Provider revision must contain a value.", nameof(providerRevision));
        }

        NormalizedText = text;
        LanguagePair = languagePair;
        ProviderRevision = revision;
    }

    public string NormalizedText { get; }

    public LanguagePair LanguagePair { get; }

    public string ProviderRevision { get; }
}

public sealed record TranslationResult
{
    public TranslationResult(TranslationRequest request, string translatedText)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        ArgumentNullException.ThrowIfNull(translatedText);
        TranslatedText = translatedText;
    }

    public TranslationRequest Request { get; }

    public string TranslatedText { get; }
}

public sealed record TranslationPublication
{
    public TranslationPublication(long generation, TranslationResult result)
    {
        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        Generation = generation;
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    public long Generation { get; }

    public TranslationResult Result { get; }
}

public interface ITextTranslator
{
    Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default);
}

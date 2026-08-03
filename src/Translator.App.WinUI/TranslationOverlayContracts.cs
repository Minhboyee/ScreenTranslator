using Windows.UI;

namespace Translator_App_WinUI;

/// <summary>Physical desktop coordinates supplied by a later OCR integration.</summary>
public readonly record struct OverlayDesktopBounds(int Left, int Top, int Width, int Height)
{
    public bool IsValid => Width > 0 && Height > 0;
}

public readonly record struct OverlayAppearance(Color Foreground, Color Background)
{
    public static OverlayAppearance Default => new(Color.FromArgb(255, 0, 0, 0), Color.FromArgb(255, 255, 255, 255));
}

public readonly record struct OverlayFont(string Family, double Size)
{
    public static OverlayFont Default => new("Segoe UI Variable", 16);
}

/// <summary>UI-only payload for one translated OCR line. No OCR or translation behavior is implied.</summary>
public sealed record TranslatedOverlayLine(
    string LineId,
    OverlayDesktopBounds DesktopBounds,
    string TranslatedText,
    OverlayAppearance Appearance,
    OverlayFont Font);

public interface ITranslationOverlaySurface : IDisposable
{
    void UpdateLines(IEnumerable<TranslatedOverlayLine> lines);
    void Clear();
    void Show();
    void Hide();
}

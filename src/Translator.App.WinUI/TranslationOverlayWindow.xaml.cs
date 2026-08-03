using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.UI;
using WinRT.Interop;

namespace Translator_App_WinUI;

/// <summary>One click-through WinUI surface containing all current line labels.</summary>
public sealed partial class TranslationOverlayWindow : Window, ITranslationOverlaySurface
{
    private const int GwlExStyle = -20;
    private const int GwlStyle = -16;
    private const int WsCaption = 0x00C00000;
    private const int WsThickFrame = 0x00040000;
    private const int WsMinimizeBox = 0x00020000;
    private const int WsMaximizeBox = 0x00010000;
    private const int WsSysMenu = 0x00080000;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTransparent = 0x00000020;
    private const int GwlpHwndParent = -8;
    private const int SwpNoMove = 0x0002;
    private const int SwpNoSize = 0x0001;
    private const int SwpNoZOrder = 0x0004;
    private const int SwpNoActivate = 0x0010;
    private const int SwpShowWindow = 0x0040;
    private const int SwpHideWindow = 0x0080;
    private const int SwpFrameChanged = 0x0020;
    private const uint WmNcHitTest = 0x0084;
    private const uint WmMouseActivate = 0x0021;
    private const int HtTransparent = -1;
    private const int MaNoActivate = 3;
    private const int MonitorDefaultToNearest = 2;
    private const double LayoutGapDip = 6;
    private const double BlockPaddingDip = 6;
    // OCR glyph bounds can leave a sizeable gap between adjacent lines. Treat the two-line
    // reading block as one unit so its replacement coverage cannot stop between glyph rows.
    private const int GroupGap = 72;
    private nint hwnd;
    private readonly nint ownerWindowHandle;
    private readonly SubclassProc subclassProc;
    private bool subclassInstalled;
    private bool disposed;

    public TranslationOverlayWindow()
        : this(0)
    {
    }

    public TranslationOverlayWindow(nint ownerWindowHandle)
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        hwnd = WindowNative.GetWindowHandle(this);
        this.ownerWindowHandle = ownerWindowHandle;
        if (ownerWindowHandle != 0)
        {
            SetWindowLongPtr(hwnd, GwlpHwndParent, ownerWindowHandle);
        }

        ConfigureBorderlessWindow();

        subclassProc = HandleSubclassMessage;
        MakeClickThrough();
        subclassInstalled = SetWindowSubclass(hwnd, subclassProc, 1, 0);
        if (!subclassInstalled)
        {
            throw new InvalidOperationException("The translation overlay could not be made click-through.");
        }
    }

    public nint OwnerWindowHandle => ownerWindowHandle;

    public bool HitTestSubclassInstalled => subclassInstalled;

    public void UpdateLines(IEnumerable<TranslatedOverlayLine> lines)
    {
        if (disposed) throw new ObjectDisposedException(nameof(TranslationOverlayWindow));
        var items = lines.Where(line => !string.IsNullOrWhiteSpace(line.LineId)
            && !string.IsNullOrWhiteSpace(line.TranslatedText)
            && line.DesktopBounds.IsValid).ToArray();
        LineCanvas.Children.Clear();
        if (items.Length == 0)
        {
            SetWindowRgn(hwnd, 0, true);
            return;
        }

        LayoutItems(items, DpiScaleForWindow(hwnd), true);
    }

    private void LayoutItems(TranslatedOverlayLine[] items, double scale, bool allowDpiRelayout)
    {
        var padding = DipToPixels(BlockPaddingDip, scale);
        var gap = DipToPixels(LayoutGapDip, scale);

        // Measuring is deliberately done before placement. The measured height includes wrapping,
        // the selected font, and the label padding; it is never inferred from font size.
        var groups = GroupLines(items);
        var placed = new List<OverlayBlock>();
        foreach (var group in groups)
        {
            var block = BuildBlock(group, placed, scale, padding, gap);
            placed.Add(block);
        }

        var left = placed.Min(block => block.Bounds.Left);
        var top = placed.Min(block => block.Bounds.Top);
        var right = placed.Max(block => block.Bounds.Right);
        var bottom = placed.Max(block => block.Bounds.Bottom);
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        appWindow.MoveAndResize(new Windows.Graphics.RectInt32(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top)));

        var targetScale = DpiScaleForWindow(hwnd);
        if (allowDpiRelayout && Math.Abs(targetScale - scale) > 0.001)
        {
            LineCanvas.Children.Clear();
            LayoutItems(items, targetScale, false);
            return;
        }

        foreach (var block in placed)
        {
            if (block.Replacement)
            {
                var coverage = new Border
                {
                    Width = PixelsToDips(block.Bounds.Width, scale),
                    Height = PixelsToDips(block.Bounds.Height, scale),
                    Background = Brush(block.Appearance.Background, 250),
                    CornerRadius = new CornerRadius(6),
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(coverage, PixelsToDips(block.Bounds.Left - left, scale));
                Canvas.SetTop(coverage, PixelsToDips(block.Bounds.Top - top, scale));
                LineCanvas.Children.Add(coverage);
            }

            foreach (var measured in block.Labels)
            {
                measured.View.Width = PixelsToDips(measured.Bounds.Width, scale);
                measured.View.Height = PixelsToDips(measured.Bounds.Height, scale);
                Canvas.SetLeft(measured.View, PixelsToDips(measured.Bounds.Left - left, scale));
                Canvas.SetTop(measured.View, PixelsToDips(measured.Bounds.Top - top, scale));
                LineCanvas.Children.Add(measured.View);
            }
        }

        LineCanvas.Width = PixelsToDips(Math.Max(1, right - left), scale);
        LineCanvas.Height = PixelsToDips(Math.Max(1, bottom - top), scale);
        ApplyPaintRegion(placed, left, top);
    }

    private static IReadOnlyList<IReadOnlyList<TranslatedOverlayLine>> GroupLines(TranslatedOverlayLine[] lines)
    {
        var groups = new List<List<TranslatedOverlayLine>>();
        foreach (var line in lines.OrderBy(item => item.DesktopBounds.Top).ThenBy(item => item.DesktopBounds.Left))
        {
            var current = groups.LastOrDefault();
            if (current is null || line.DesktopBounds.Top > current.Max(item => item.DesktopBounds.Top + item.DesktopBounds.Height) + GroupGap)
                groups.Add(current = []);
            current.Add(line);
        }

        return groups;
    }

    private OverlayBlock BuildBlock(IReadOnlyList<TranslatedOverlayLine> group, IReadOnlyList<OverlayBlock> placed,
        double scale, int padding, int gap)
    {
        var source = SourceUnion(group);
        var workArea = WorkAreaFor(source);
        var availableWidth = Math.Max(40, source.Width);
        var labels = group.Select(line => MeasureLabel(line, availableWidth, scale, padding)).ToArray();
        var stackWidth = labels.Max(label => label.Bounds.Width);
        var stackHeight = labels.Sum(label => label.Bounds.Height) + gap * Math.Max(0, labels.Length - 1);
        var above = new OverlayRect(
            source.Left - padding,
            source.Top - padding - stackHeight - gap,
            Math.Max(source.Width + padding * 2, stackWidth + padding * 2),
            stackHeight + padding * 2);

        var useAbove = TryFindAbove(above, source, workArea, placed, gap, out var abovePlacement);
        var bounds = useAbove
            ? abovePlacement
            : new OverlayRect(source.Left - padding, source.Top - padding,
                Math.Max(source.Width + padding * 2, stackWidth + padding * 2),
                Math.Max(source.Height + padding * 2, stackHeight + padding * 2));

        // A replacement block is near-opaque and covers the entire source union, so source glyphs
        // cannot bleed through. It is only used when the measured stack cannot safely sit above.
        if (!useAbove)
        {
            bounds = MoveReplacementAwayFromPriorBlocks(bounds, source, workArea, placed);
        }

        var labelX = bounds.Left + padding;
        var labelY = bounds.Top + padding;
        foreach (var label in labels)
        {
            label.Bounds = new OverlayRect(labelX, labelY, label.Bounds.Width, label.Bounds.Height);
            label.View.Background = Brush(label.Line.Appearance.Background, useAbove ? (byte)232 : (byte)250);
            labelY += label.Bounds.Height + gap;
        }

        return new OverlayBlock(bounds, labels, !useAbove, group[0].Appearance);
    }

    private static bool TryFindAbove(
        OverlayRect preferred, OverlayRect source, OverlayRect workArea,
        IReadOnlyList<OverlayBlock> placed, int gap, out OverlayRect result)
    {
        // Search upward first. This keeps a clear gap from the source while avoiding an earlier
        // block; replacement coverage is the fallback when the work area has no safe slot.
        for (var step = 0; step <= 256; step++)
        {
            var candidate = preferred with { Top = preferred.Top - step * 4 };
            if (candidate.Bottom > source.Top - gap) continue;
            if (Fits(candidate, workArea) && !placed.Any(block => candidate.Intersects(block.Bounds)))
            {
                result = candidate;
                return true;
            }
        }

        result = preferred;
        return false;
    }

    private static OverlayRect MoveReplacementAwayFromPriorBlocks(
        OverlayRect candidate, OverlayRect source, OverlayRect workArea, IReadOnlyList<OverlayBlock> placed)
    {
        if (!placed.Any(block => candidate.Intersects(block.Bounds))) return candidate;

        // Keep source coverage as the first priority. If padding collides with an earlier block,
        // try the nearest valid vertical positions in a stable order.
        foreach (var delta in Enumerable.Range(1, 64).SelectMany(step => new[] { -step * 4, step * 4 }))
        {
            var shiftedTop = candidate.Top + delta;
            var shifted = candidate with
            {
                Top = shiftedTop,
                Height = Math.Max(candidate.Bottom, source.Bottom) - shiftedTop
            };
            if (shifted.Top <= source.Top && Fits(shifted, workArea)
                && !placed.Any(block => shifted.Intersects(block.Bounds))) return shifted;
        }

        return candidate;
    }

    private MeasuredLabel MeasureLabel(TranslatedOverlayLine line, int maxWidth, double scale, int padding)
    {
        var text = new TextBlock
        {
            Text = line.TranslatedText,
            FontFamily = new FontFamily(string.IsNullOrWhiteSpace(line.Font.Family) ? "Segoe UI Variable" : line.Font.Family),
            FontSize = Math.Max(10, line.Font.Size),
            Foreground = Brush(line.Appearance.Foreground),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = Math.Max(40 / scale, PixelsToDips(maxWidth - padding * 2, scale))
        };
        var view = new Border
        {
            Child = text,
            Background = Brush(line.Appearance.Background, 232),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(PixelsToDips(padding, scale), 2, PixelsToDips(padding, scale), 2),
            IsHitTestVisible = false
        };
        view.Measure(new Windows.Foundation.Size(Math.Max(40 / scale, PixelsToDips(maxWidth, scale)), double.PositiveInfinity));
        var desired = view.DesiredSize;
        return new MeasuredLabel(line, view, new OverlayRect(0, 0,
            Math.Max(1, DipToPixels(desired.Width, scale)), Math.Max(1, DipToPixels(desired.Height, scale))));
    }

    private static int DipToPixels(double dips, double scale) => Math.Max(1, (int)Math.Ceiling(dips * scale));
    private static double PixelsToDips(int pixels, double scale) => pixels / scale;
    private static double DpiScaleForWindow(nint windowHandle) => Math.Max(1u, GetDpiForWindow(windowHandle)) / 96d;

    private static OverlayRect SourceUnion(IReadOnlyList<TranslatedOverlayLine> lines)
    {
        var left = lines.Min(line => line.DesktopBounds.Left);
        var top = lines.Min(line => line.DesktopBounds.Top);
        var right = lines.Max(line => line.DesktopBounds.Left + line.DesktopBounds.Width);
        var bottom = lines.Max(line => line.DesktopBounds.Top + line.DesktopBounds.Height);
        return new OverlayRect(left, top, right - left, bottom - top);
    }

    private static OverlayRect WorkAreaFor(OverlayRect bounds)
    {
        var monitor = MonitorFromPoint(new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2), MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != 0 && GetMonitorInfo(monitor, ref info))
            return new OverlayRect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        return new OverlayRect(bounds.Left - 2000, bounds.Top - 2000, 4000, 4000);
    }

    private static bool Fits(OverlayRect rect, OverlayRect area) =>
        rect.Left >= area.Left && rect.Top >= area.Top && rect.Right <= area.Right && rect.Bottom <= area.Bottom;

    private sealed class MeasuredLabel(TranslatedOverlayLine line, Border view, OverlayRect bounds)
    {
        public TranslatedOverlayLine Line { get; } = line;
        public Border View { get; } = view;
        public OverlayRect Bounds { get; set; } = bounds;
    }

    private sealed record OverlayBlock(OverlayRect Bounds, IReadOnlyList<MeasuredLabel> Labels, bool Replacement, OverlayAppearance Appearance);
    private readonly record struct OverlayRect(int Left, int Top, int Width, int Height)
    {
        public int Right => Left + Width;
        public int Bottom => Top + Height;
        public bool Intersects(OverlayRect other) => Left < other.Right && Right > other.Left && Top < other.Bottom && Bottom > other.Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Point { public Point(int x, int y) { X = x; Y = y; } public readonly int X; public readonly int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo { public int Size; public MonitorRect Monitor; public MonitorRect Work; public int Flags; }

    public void Clear()
    {
        LineCanvas.Children.Clear();
        SetWindowRgn(hwnd, 0, true);
    }
    public void Show() => SetVisible(true);
    public void Hide() => SetVisible(false);

    private void SetVisible(bool visible)
    {
        if (disposed) return;
        SetWindowPos(
            hwnd,
            0,
            0,
            0,
            0,
            0,
            SwpNoMove |
            SwpNoSize |
            SwpNoActivate |
            SwpNoZOrder |
            (visible ? SwpShowWindow : SwpHideWindow));
    }

    private void MakeClickThrough()
    {
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlExStyle, new nint(style | WsExNoActivate | WsExToolWindow | WsExTransparent));
    }

    private void ConfigureBorderlessWindow()
    {
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
        }

        // Also remove the native caption styles. This keeps the overlay borderless even
        // while the presenter is being applied and guarantees there are no caption buttons.
        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlStyle, new nint(style &
            ~(WsCaption | WsThickFrame | WsMinimizeBox | WsMaximizeBox | WsSysMenu)));
        SetWindowPos(hwnd, 0, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);

    }

    private void ApplyPaintRegion(IReadOnlyList<OverlayBlock> blocks, int left, int top)
    {
        nint region = 0;
        try
        {
            foreach (var block in blocks)
            {
                var rectangles = block.Replacement
                    ? new[] { block.Bounds }
                    : block.Labels.Select(label => label.Bounds);
                foreach (var rectangle in rectangles)
                {
                    var part = CreateRectRgn(rectangle.Left - left, rectangle.Top - top,
                        rectangle.Right - left, rectangle.Bottom - top);
                    if (part == 0) continue;
                    if (region == 0)
                    {
                        region = part;
                    }
                    else
                    {
                        CombineRgn(region, region, part, RgnOr);
                        DeleteObject(part);
                    }
                }
            }

            if (SetWindowRgn(hwnd, region, true) != 0)
                region = 0; // ownership transfers to the window
        }
        finally
        {
            if (region != 0) DeleteObject(region);
        }
    }

    private static SolidColorBrush Brush(Color color, byte? alpha = null) =>
        new(Color.FromArgb(alpha ?? color.A, color.R, color.G, color.B));

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (subclassInstalled)
        {
            RemoveWindowSubclass(hwnd, subclassProc, 1);
            subclassInstalled = false;
        }

        Clear();
        Close();
    }

    private nint HandleSubclassMessage(
        nint windowHandle,
        uint message,
        nint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData)
    {
        return message switch
        {
            WmNcHitTest => HtTransparent,
            WmMouseActivate => MaNoActivate,
            _ => DefSubclassProc(windowHandle, message, wParam, lParam)
        };
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, int flags);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);
    [DllImport("gdi32.dll")]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(nint destination, nint source1, nint source2, int mode);
    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint hWnd, nint region, bool redraw);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint handle);
    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(Point point, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    private const int RgnOr = 2;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(
        nint windowHandle,
        uint message,
        nint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint windowHandle,
        SubclassProc callback,
        nuint subclassId,
        nuint referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint windowHandle,
        SubclassProc callback,
        nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(
        nint windowHandle,
        uint message,
        nint wParam,
        nint lParam);
}

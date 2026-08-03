using System.Runtime.InteropServices;

namespace Translator.Windows;

public readonly record struct DipSize
{
    public DipSize(double width, double height)
    {
        ValidateFiniteNonNegative(width, nameof(width));
        ValidateFiniteNonNegative(height, nameof(height));
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        Width = width;
        Height = height;
    }

    public double Width { get; }

    public double Height { get; }

    private static void ValidateFiniteNonNegative(double value, string parameterName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

public readonly record struct DipRect
{
    public DipRect(double left, double top, double width, double height)
    {
        ValidateFinite(left, nameof(left));
        ValidateFinite(top, nameof(top));
        ValidateFiniteNonNegative(width, nameof(width));
        ValidateFiniteNonNegative(height, nameof(height));
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public double Left { get; }

    public double Top { get; }

    public double Width { get; }

    public double Height { get; }

    public double Right => Left + Width;

    public double Bottom => Top + Height;

    private static void ValidateFinite(double value, string parameterName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateFiniteNonNegative(double value, string parameterName)
    {
        ValidateFinite(value, parameterName);
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

public readonly record struct CaptureItemPixelSize
{
    public CaptureItemPixelSize(int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }
}

public readonly record struct ItemLocalCropRect
{
    public ItemLocalCropRect(int left, int top, int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
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

public readonly record struct DesktopScreenSelectionRect
{
    public DesktopScreenSelectionRect(int left, int top, int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
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

public readonly record struct DesktopWorkAreaRect
{
    public DesktopWorkAreaRect(int left, int top, int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
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

public readonly record struct DesktopCardRect
{
    public DesktopCardRect(int left, int top, int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
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

public enum CardSide
{
    Below,
    Above
}

public sealed record SingleCardPlacement
{
    public SingleCardPlacement(CardSide side, DesktopCardRect bounds)
    {
        Side = side;
        Bounds = bounds;
    }

    public CardSide Side { get; }

    public DesktopCardRect Bounds { get; }
}

public static class CaptureCropContract
{
    public static ItemLocalCropRect FullItem(CaptureItemPixelSize itemSize)
    {
        return new ItemLocalCropRect(0, 0, itemSize.Width, itemSize.Height);
    }

    public static ItemLocalCropRect Validate(
        ItemLocalCropRect crop,
        CaptureItemPixelSize itemSize)
    {
        if (crop.Left < 0 || crop.Top < 0 || crop.Right > itemSize.Width || crop.Bottom > itemSize.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(crop), "The crop must be fully inside the capture item.");
        }

        return crop;
    }
}

public readonly record struct BitmapSourceCropBounds(
    uint X,
    uint Y,
    uint Width,
    uint Height);

public static class SoftwareBitmapCropContract
{
    public static BitmapSourceCropBounds ToSourceBounds(
        ItemLocalCropRect crop,
        CaptureItemPixelSize contentSize)
    {
        CaptureCropContract.Validate(crop, contentSize);
        return new BitmapSourceCropBounds(
            checked((uint)crop.Left),
            checked((uint)crop.Top),
            checked((uint)crop.Width),
            checked((uint)crop.Height));
    }

    public static bool IsContentSizeCompatible(
        WindowCaptureSelection selection,
        CaptureItemPixelSize contentSize)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return selection.ItemPixelSize == contentSize &&
               contentSize.Width >= selection.ItemLocalCrop.Right &&
               contentSize.Height >= selection.ItemLocalCrop.Bottom;
    }
}

public static class DipToItemPixelTransform
{
    public static ItemLocalCropRect ToItemPixelCrop(
        DipRect selection,
        DipSize imageDipSize,
        CaptureItemPixelSize itemPixelSize)
    {
        if (selection.Left < 0 || selection.Top < 0 || selection.Right > imageDipSize.Width || selection.Bottom > imageDipSize.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(selection), "The selection must be inside the image bounds.");
        }

        var left = ClampToPixels(
            Math.Floor(selection.Left / imageDipSize.Width * itemPixelSize.Width),
            itemPixelSize.Width);
        var top = ClampToPixels(
            Math.Floor(selection.Top / imageDipSize.Height * itemPixelSize.Height),
            itemPixelSize.Height);
        var right = ClampToPixels(
            Math.Ceiling(selection.Right / imageDipSize.Width * itemPixelSize.Width),
            itemPixelSize.Width);
        var bottom = ClampToPixels(
            Math.Ceiling(selection.Bottom / imageDipSize.Height * itemPixelSize.Height),
            itemPixelSize.Height);

        if (right <= left)
        {
            right = Math.Min(itemPixelSize.Width, left + 1);
        }

        if (bottom <= top)
        {
            bottom = Math.Min(itemPixelSize.Height, top + 1);
        }

        return CaptureCropContract.Validate(
            new ItemLocalCropRect(left, top, right - left, bottom - top),
            itemPixelSize);
    }

    private static int ClampToPixels(double edge, int pixelEdge)
    {
        return (int)Math.Clamp(edge, 0, pixelEdge);
    }
}

public readonly record struct CaptureSelectionCoordinates(
    ItemLocalCropRect ItemLocalCrop,
    DesktopScreenSelectionRect DesktopScreenSelection);

public static class CaptureCoordinateTransform
{
    public static CaptureSelectionCoordinates MapImageSelection(
        DipRect imageSelection,
        DipSize imageDipSize,
        CaptureItemPixelSize itemPixelSize,
        DesktopScreenSelectionRect desktopBounds)
    {
        var itemCrop = DipToItemPixelTransform.ToItemPixelCrop(
            imageSelection,
            imageDipSize,
            itemPixelSize);
        return new CaptureSelectionCoordinates(
            itemCrop,
            ToDesktopSelection(itemCrop, itemPixelSize, desktopBounds));
    }

    public static DesktopScreenSelectionRect ToDesktopSelection(
        ItemLocalCropRect itemCrop,
        CaptureItemPixelSize itemPixelSize,
        DesktopScreenSelectionRect desktopBounds)
    {
        CaptureCropContract.Validate(itemCrop, itemPixelSize);
        var left = ClampToDesktopEdge(
            desktopBounds.Left + itemCrop.Left / (double)itemPixelSize.Width * desktopBounds.Width,
            desktopBounds.Left,
            desktopBounds.Right,
            roundUp: false);
        var top = ClampToDesktopEdge(
            desktopBounds.Top + itemCrop.Top / (double)itemPixelSize.Height * desktopBounds.Height,
            desktopBounds.Top,
            desktopBounds.Bottom,
            roundUp: false);
        var right = ClampToDesktopEdge(
            desktopBounds.Left + itemCrop.Right / (double)itemPixelSize.Width * desktopBounds.Width,
            desktopBounds.Left,
            desktopBounds.Right,
            roundUp: true);
        var bottom = ClampToDesktopEdge(
            desktopBounds.Top + itemCrop.Bottom / (double)itemPixelSize.Height * desktopBounds.Height,
            desktopBounds.Top,
            desktopBounds.Bottom,
            roundUp: true);

        return new DesktopScreenSelectionRect(left, top, right - left, bottom - top);
    }

    private static int ClampToDesktopEdge(
        double edge,
        int minimum,
        int maximum,
        bool roundUp)
    {
        var rounded = roundUp ? Math.Ceiling(edge) : Math.Floor(edge);
        return (int)Math.Clamp(rounded, minimum, maximum);
    }
}

public static class WindowCaptureSnapshotContract
{
    public static void ValidateMetadata(
        nint chromeWindowHandle,
        CaptureItemPixelSize itemPixelSize,
        CaptureItemPixelSize bitmapPixelSize,
        DesktopScreenSelectionRect extendedFrameBounds)
    {
        ValidateWindowGeometry(chromeWindowHandle, extendedFrameBounds);

        if (itemPixelSize != bitmapPixelSize)
        {
            throw new InvalidOperationException("The snapshot bitmap size does not match the capture item size.");
        }
    }

    public static void ValidateWindowGeometry(
        nint chromeWindowHandle,
        DesktopScreenSelectionRect extendedFrameBounds)
    {
        if (chromeWindowHandle == 0)
        {
            throw new ArgumentException("A Chrome window handle is required.", nameof(chromeWindowHandle));
        }

        if (extendedFrameBounds.Width <= 0 || extendedFrameBounds.Height <= 0)
        {
            throw new InvalidOperationException("The snapshot window bounds are invalid.");
        }
    }
}

public sealed class SingleFrameCaptureGuard
{
    private const int Waiting = 0;
    private const int FrameClaimed = 1;
    private const int Terminal = 2;
    private int state;

    public bool TryClaimFrame()
    {
        return Interlocked.CompareExchange(ref state, FrameClaimed, Waiting) == Waiting;
    }

    public bool TryComplete()
    {
        return Interlocked.CompareExchange(ref state, Terminal, FrameClaimed) == FrameClaimed;
    }

    public bool TryCancel()
    {
        return Interlocked.Exchange(ref state, Terminal) != Terminal;
    }

    public bool HasClaimedFrame => Volatile.Read(ref state) == FrameClaimed;

    public bool IsTerminal => Volatile.Read(ref state) == Terminal;
}

public static class SingleCardPlacementCalculator
{
    public static SingleCardPlacement? Place(
        DesktopScreenSelectionRect selection,
        DesktopWorkAreaRect workArea,
        CardSide side,
        int preferredWidth,
        int preferredHeight,
        int maxVisibleHeight,
        int gap = 8)
    {
        if (preferredWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(preferredWidth));
        }

        if (preferredHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(preferredHeight));
        }

        if (maxVisibleHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxVisibleHeight));
        }

        if (gap < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gap));
        }

        var width = Math.Min(preferredWidth, workArea.Width);
        var left = Math.Clamp(selection.Left, workArea.Left, workArea.Right - width);
        var availableHeight = side switch
        {
            CardSide.Below => workArea.Bottom - selection.Bottom - gap,
            CardSide.Above => selection.Top - workArea.Top - gap,
            _ => throw new ArgumentOutOfRangeException(nameof(side))
        };

        if (availableHeight <= 0)
        {
            return null;
        }

        var height = Math.Min(Math.Min(preferredHeight, maxVisibleHeight), availableHeight);
        if (height <= 0)
        {
            return null;
        }

        var top = side == CardSide.Below
            ? selection.Bottom + gap
            : selection.Top - gap - height;
        return new SingleCardPlacement(side, new DesktopCardRect(left, top, width, height));
    }
}

public sealed record WindowCaptureSelection
{
    public WindowCaptureSelection(
        nint windowHandle,
        CaptureItemPixelSize itemPixelSize,
        ItemLocalCropRect itemLocalCrop,
        DesktopScreenSelectionRect desktopScreenSelection,
        long epoch)
    {
        if (windowHandle == 0)
        {
            throw new ArgumentException("A window handle is required.", nameof(windowHandle));
        }

        if (epoch <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(epoch));
        }

        CaptureCropContract.Validate(itemLocalCrop, itemPixelSize);
        WindowHandle = windowHandle;
        ItemPixelSize = itemPixelSize;
        ItemLocalCrop = itemLocalCrop;
        DesktopScreenSelection = desktopScreenSelection;
        Epoch = epoch;
    }

    public nint WindowHandle { get; }

    public CaptureItemPixelSize ItemPixelSize { get; }

    public ItemLocalCropRect ItemLocalCrop { get; }

    public DesktopScreenSelectionRect DesktopScreenSelection { get; }

    public long Epoch { get; }
}

public sealed record WindowCaptureSelectionInvalidation
{
    public WindowCaptureSelectionInvalidation(long epoch, string reason)
    {
        if (epoch <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(epoch));
        }

        ArgumentNullException.ThrowIfNull(reason);
        if (reason.Trim().Length == 0)
        {
            throw new ArgumentException("A reason is required.", nameof(reason));
        }

        Epoch = epoch;
        Reason = reason.Trim();
    }

    public long Epoch { get; }

    public string Reason { get; }
}

public sealed class SelectionEpochGate
{
    private long currentEpoch;

    public long CurrentEpoch => Volatile.Read(ref currentEpoch);

    public long BeginSelection()
    {
        return Interlocked.Increment(ref currentEpoch);
    }

    public bool IsCurrent(long epoch)
    {
        return epoch > 0 && Volatile.Read(ref currentEpoch) == epoch;
    }
}

public readonly record struct ChromeWindowMetadata(
    nint WindowHandle,
    string WindowClass,
    string Title,
    bool IsVisible,
    bool IsTopLevel);

public sealed record ChromeWindowInfo
{
    public ChromeWindowInfo(
        nint windowHandle,
        string title,
        DesktopScreenSelectionRect extendedFrameBounds)
    {
        if (windowHandle == 0)
        {
            throw new ArgumentException("A window handle is required.", nameof(windowHandle));
        }

        ArgumentNullException.ThrowIfNull(title);
        WindowHandle = windowHandle;
        Title = title;
        ExtendedFrameBounds = extendedFrameBounds;
    }

    public nint WindowHandle { get; }

    public string Title { get; }

    public DesktopScreenSelectionRect ExtendedFrameBounds { get; }
}

public static class ChromeWindowEnumerator
{
    private const string ChromeWindowClassPrefix = "Chrome_WidgetWin_";

    public static IReadOnlyList<ChromeWindowInfo> EnumerateVisibleTopLevel()
    {
        var windows = new List<ChromeWindowInfo>();
        NativeMethods.EnumWindows(
            (windowHandle, _) =>
            {
                var metadata = new ChromeWindowMetadata(
                    windowHandle,
                    GetWindowClass(windowHandle),
                    GetWindowTitle(windowHandle),
                    NativeMethods.IsWindowVisible(windowHandle),
                    NativeMethods.GetWindow(windowHandle, NativeMethods.GwOwner) == 0 &&
                    NativeMethods.GetParent(windowHandle) == 0);
                if (!IsCandidate(metadata) ||
                    !WindowGeometry.TryGetExtendedFrameBounds(windowHandle, out var bounds))
                {
                    return true;
                }

                windows.Add(new ChromeWindowInfo(windowHandle, metadata.Title, bounds));
                return true;
            },
            0);
        return windows;
    }

    public static bool IsCandidate(ChromeWindowMetadata metadata)
    {
        return metadata.WindowHandle != 0 &&
               metadata.IsVisible &&
               metadata.IsTopLevel &&
               !string.IsNullOrWhiteSpace(metadata.Title) &&
               !string.IsNullOrWhiteSpace(metadata.WindowClass) &&
               metadata.WindowClass.StartsWith(ChromeWindowClassPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetWindowTitle(nint windowHandle)
    {
        var length = NativeMethods.GetWindowTextLength(windowHandle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(length + 1);
        NativeMethods.GetWindowText(windowHandle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string GetWindowClass(nint windowHandle)
    {
        var builder = new System.Text.StringBuilder(256);
        NativeMethods.GetClassName(windowHandle, builder, builder.Capacity);
        return builder.ToString();
    }
}

public sealed record MonitorWorkAreaInfo
{
    public MonitorWorkAreaInfo(
        nint monitorHandle,
        DesktopWorkAreaRect workArea,
        uint dpiX,
        uint dpiY)
    {
        if (monitorHandle == 0)
        {
            throw new ArgumentException("A monitor handle is required.", nameof(monitorHandle));
        }

        if (dpiX == 0 || dpiY == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpiX));
        }

        MonitorHandle = monitorHandle;
        WorkArea = workArea;
        DpiX = dpiX;
        DpiY = dpiY;
    }

    public nint MonitorHandle { get; }

    public DesktopWorkAreaRect WorkArea { get; }

    public uint DpiX { get; }

    public uint DpiY { get; }
}

public static class MonitorWorkAreaLookup
{
    public static MonitorWorkAreaInfo ForSelection(DesktopScreenSelectionRect selection)
    {
        var x = selection.Left + selection.Width / 2;
        var y = selection.Top + selection.Height / 2;
        return ForPoint(x, y);
    }

    public static MonitorWorkAreaInfo ForPoint(int x, int y)
    {
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.Point(x, y), NativeMethods.MonitorDefaultToNearest);
        if (monitor == 0)
        {
            throw new InvalidOperationException("The monitor for the selection could not be found.");
        }

        var monitorInfo = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            throw new InvalidOperationException("The monitor work area could not be read.");
        }

        var dpiX = 96u;
        var dpiY = 96u;
        if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MdtEffectiveDpi, out var reportedDpiX, out var reportedDpiY) == 0 &&
            reportedDpiX > 0 &&
            reportedDpiY > 0)
        {
            dpiX = reportedDpiX;
            dpiY = reportedDpiY;
        }

        var workArea = monitorInfo.WorkArea;
        return new MonitorWorkAreaInfo(
            monitor,
            new DesktopWorkAreaRect(
                workArea.Left,
                workArea.Top,
                checked(workArea.Right - workArea.Left),
                checked(workArea.Bottom - workArea.Top)),
            dpiX,
            dpiY);
    }
}

public static class WindowGeometry
{
    public static DesktopScreenSelectionRect GetExtendedFrameBounds(nint windowHandle)
    {
        if (!TryGetExtendedFrameBounds(windowHandle, out var bounds))
        {
            throw new InvalidOperationException("The DWM extended-frame bounds could not be read.");
        }

        return bounds;
    }

    public static bool TryGetExtendedFrameBounds(
        nint windowHandle,
        out DesktopScreenSelectionRect bounds)
    {
        bounds = default;
        if (windowHandle == 0 || NativeMethods.DwmGetWindowAttribute(
                windowHandle,
                NativeMethods.DwmExtendedFrameBounds,
                out var nativeBounds,
                Marshal.SizeOf<NativeMethods.Rect>()) != 0)
        {
            return false;
        }

        var width = nativeBounds.Right - nativeBounds.Left;
        var height = nativeBounds.Bottom - nativeBounds.Top;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        bounds = new DesktopScreenSelectionRect(nativeBounds.Left, nativeBounds.Top, width, height);
        return true;
    }
}

internal static class NativeMethods
{
    internal const uint DwmExtendedFrameBounds = 9;
    internal const uint GaRoot = 2;
    internal const uint GwOwner = 4;
    internal const uint MonitorDefaultToNearest = 2;
    internal const int MdtEffectiveDpi = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct Point(int x, int y)
    {
        internal int X { get; } = x;
        internal int Y { get; } = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct Rect
    {
        internal int Left { get; init; }
        internal int Top { get; init; }
        internal int Right { get; init; }
        internal int Bottom { get; init; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfo
    {
        internal int Size;
        internal Rect Monitor;
        internal Rect WorkArea;
        internal uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate bool EnumWindowsProc(nint windowHandle, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    internal static extern nint GetAncestor(nint windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowTextLength(nint windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowText(nint windowHandle, System.Text.StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassName(nint windowHandle, System.Text.StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    internal static extern nint GetWindow(nint windowHandle, uint command);

    [DllImport("user32.dll")]
    internal static extern nint GetParent(nint windowHandle);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(
        nint windowHandle,
        uint attribute,
        out Rect value,
        int valueSize);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(
        nint monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);
}

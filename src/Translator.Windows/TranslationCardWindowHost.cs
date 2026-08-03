using System.Runtime.InteropServices;

namespace Translator.Windows;

public readonly record struct TranslationCardWindowInteropRequest(
    nint OwnerWindowHandle,
    uint RequiredExtendedStyles,
    uint PositionFlags,
    DesktopCardRect? Bounds);

public static class TranslationCardWindowInterop
{
    public const uint WsExTransparent = 0x00000020;
    public const uint WsExToolWindow = 0x00000080;
    public const uint WsExNoActivate = 0x08000000;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpFrameChanged = 0x0020;
    public const uint SwpShowWindow = 0x0040;
    public const uint SwpHideWindow = 0x0080;
    public const uint WmNcHitTest = 0x0084;
    public const int HtTransparent = -1;
    public const uint WmMouseActivate = 0x0021;
    public const int MaNoActivate = 3;

    public const uint RequiredExtendedStyles =
        WsExNoActivate | WsExToolWindow | WsExTransparent;

    public static TranslationCardWindowInteropRequest ComposeRequest(
        nint ownerWindowHandle,
        SingleCardPlacement? placement)
    {
        if (ownerWindowHandle == 0)
        {
            throw new ArgumentException("An owner window handle is required.", nameof(ownerWindowHandle));
        }

        var positionFlags = SwpNoZOrder | SwpNoActivate;
        positionFlags |= placement is null
            ? SwpNoMove | SwpNoSize | SwpHideWindow
            : SwpShowWindow;
        return new TranslationCardWindowInteropRequest(
            ownerWindowHandle,
            RequiredExtendedStyles,
            positionFlags,
            placement?.Bounds);
    }

    public static nint HandleMessage(uint message)
    {
        return message switch
        {
            WmNcHitTest => HtTransparent,
            WmMouseActivate => MaNoActivate,
            _ => nint.MinValue
        };
    }
}

public sealed class TranslationCardPlacementState
{
    private readonly object gate = new();
    private SingleCardPlacement? currentPlacement;

    public TranslationCardPlacementState(
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

        Side = side;
        PreferredWidth = preferredWidth;
        PreferredHeight = preferredHeight;
        MaxVisibleHeight = maxVisibleHeight;
        Gap = gap;
    }

    public CardSide Side { get; }

    public int PreferredWidth { get; }

    public int PreferredHeight { get; }

    public int MaxVisibleHeight { get; }

    public int Gap { get; }

    public SingleCardPlacement? CurrentPlacement
    {
        get
        {
            lock (gate)
            {
                return currentPlacement;
            }
        }
    }

    public SingleCardPlacement? Update(
        DesktopScreenSelectionRect selection,
        DesktopWorkAreaRect workArea)
    {
        var placement = SingleCardPlacementCalculator.Place(
            selection,
            workArea,
            Side,
            PreferredWidth,
            PreferredHeight,
            MaxVisibleHeight,
            Gap);
        lock (gate)
        {
            currentPlacement = placement;
            return currentPlacement;
        }
    }

    public void Hide()
    {
        lock (gate)
        {
            currentPlacement = null;
        }
    }
}

public sealed class TranslationCardWindowHost : IDisposable
{
    private readonly object gate = new();
    private readonly nint cardWindowHandle;
    private readonly nint ownerWindowHandle;
    private readonly CardWindowNativeMethods.SubclassProc subclassProc;
    private SingleCardPlacement? currentPlacement;
    private bool isVisible;
    private bool subclassInstalled;
    private bool disposed;

    public TranslationCardWindowHost(nint cardWindowHandle, nint chromeTargetWindowHandle)
    {
        if (cardWindowHandle == 0)
        {
            throw new ArgumentException("A card window handle is required.", nameof(cardWindowHandle));
        }

        if (chromeTargetWindowHandle == 0)
        {
            throw new ArgumentException("A Chrome target window handle is required.", nameof(chromeTargetWindowHandle));
        }

        this.cardWindowHandle = cardWindowHandle;
        ownerWindowHandle = chromeTargetWindowHandle;
        subclassProc = HandleSubclassMessage;

        ApplyOwnerAndStyles();
        subclassInstalled = CardWindowNativeMethods.SetWindowSubclass(
            cardWindowHandle,
            subclassProc,
            1,
            0);
    }

    public nint CardWindowHandle => cardWindowHandle;

    public nint OwnerWindowHandle => ownerWindowHandle;

    public bool HitTestSubclassInstalled => subclassInstalled;

    public bool IsVisible
    {
        get
        {
            lock (gate)
            {
                return isVisible;
            }
        }
    }

    public SingleCardPlacement? CurrentPlacement
    {
        get
        {
            lock (gate)
            {
                return currentPlacement;
            }
        }
    }

    public void UpdatePlacement(SingleCardPlacement? placement)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var request = TranslationCardWindowInterop.ComposeRequest(ownerWindowHandle, placement);
            var bounds = placement?.Bounds;
            if (!CardWindowNativeMethods.SetWindowPos(
                    cardWindowHandle,
                    0,
                    bounds?.Left ?? 0,
                    bounds?.Top ?? 0,
                    bounds?.Width ?? 0,
                    bounds?.Height ?? 0,
                    request.PositionFlags))
            {
                throw new InvalidOperationException("The translation card window could not be positioned.");
            }

            currentPlacement = placement;
            isVisible = placement is not null;
        }
    }

    public void Show()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (currentPlacement is null)
            {
                return;
            }

            CardWindowNativeMethods.ShowWindow(cardWindowHandle, CardWindowNativeMethods.SwShownoactivate);
            isVisible = true;
        }
    }

    public void Hide()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            CardWindowNativeMethods.ShowWindow(cardWindowHandle, CardWindowNativeMethods.SwHide);
            isVisible = false;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            CardWindowNativeMethods.ShowWindow(cardWindowHandle, CardWindowNativeMethods.SwHide);
            if (subclassInstalled)
            {
                CardWindowNativeMethods.RemoveWindowSubclass(cardWindowHandle, subclassProc, 1);
                subclassInstalled = false;
            }

            isVisible = false;
            disposed = true;
        }
    }

    private void ApplyOwnerAndStyles()
    {
        SetWindowLongPtrChecked(
            CardWindowNativeMethods.GwlpHwndParent,
            ownerWindowHandle,
            "The Chrome target could not be assigned as the card owner.");

        var styles = CardWindowNativeMethods.GetWindowLongPtr(
            cardWindowHandle,
            CardWindowNativeMethods.GwlExstyle).ToInt64();
        var requiredStyles = styles | TranslationCardWindowInterop.RequiredExtendedStyles;
        SetWindowLongPtrChecked(
            CardWindowNativeMethods.GwlExstyle,
            new nint(requiredStyles),
            "The translation card window styles could not be applied.");

        if (!CardWindowNativeMethods.SetWindowPos(
                cardWindowHandle,
                0,
                0,
                0,
                0,
                0,
                CardWindowNativeMethods.SwpNoMove |
                CardWindowNativeMethods.SwpNoSize |
                CardWindowNativeMethods.SwpNoZOrder |
                CardWindowNativeMethods.SwpNoActivate |
                TranslationCardWindowInterop.SwpFrameChanged))
        {
            throw new InvalidOperationException("The translation card window frame could not be refreshed.");
        }
    }

    private void SetWindowLongPtrChecked(int index, nint value, string message)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = CardWindowNativeMethods.SetWindowLongPtr(cardWindowHandle, index, value);
        if (previous == 0 && Marshal.GetLastPInvokeError() != 0)
        {
            throw new InvalidOperationException(message);
        }
    }

    private nint HandleSubclassMessage(
        nint windowHandle,
        uint message,
        nint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData)
    {
        var result = TranslationCardWindowInterop.HandleMessage(message);
        return result != nint.MinValue
            ? result
            : CardWindowNativeMethods.DefSubclassProc(windowHandle, message, wParam, lParam);
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(TranslationCardWindowHost));
        }
    }
}

internal static class CardWindowNativeMethods
{
    internal const int GwlExstyle = -20;
    internal const int GwlpHwndParent = -8;
    internal const int SwHide = 0;
    internal const int SwShownoactivate = 4;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint SubclassProc(
        nint windowHandle,
        uint message,
        nint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern nint SetWindowLongPtr(nint windowHandle, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowSubclass(
        nint windowHandle,
        SubclassProc callback,
        nuint subclassId,
        nuint referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveWindowSubclass(
        nint windowHandle,
        SubclassProc callback,
        nuint subclassId);

    [DllImport("comctl32.dll")]
    internal static extern nint DefSubclassProc(
        nint windowHandle,
        uint message,
        nint wParam,
        nint lParam);
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace Translator.Windows;

public sealed class WindowCaptureSnapshot : IDisposable
{
    private SoftwareBitmap? softwareBitmap;

    public WindowCaptureSnapshot(
        SoftwareBitmap softwareBitmap,
        CaptureItemPixelSize itemPixelSize,
        nint chromeWindowHandle,
        DesktopScreenSelectionRect extendedFrameBounds)
    {
        ArgumentNullException.ThrowIfNull(softwareBitmap);
        var bitmapPixelSize = new CaptureItemPixelSize(softwareBitmap.PixelWidth, softwareBitmap.PixelHeight);
        WindowCaptureSnapshotContract.ValidateMetadata(
            chromeWindowHandle,
            itemPixelSize,
            bitmapPixelSize,
            extendedFrameBounds);

        this.softwareBitmap = softwareBitmap;
        ItemPixelSize = itemPixelSize;
        ChromeWindowHandle = chromeWindowHandle;
        ExtendedFrameBounds = extendedFrameBounds;
    }

    public SoftwareBitmap SoftwareBitmap => softwareBitmap
        ?? throw new ObjectDisposedException(nameof(WindowCaptureSnapshot));

    public CaptureItemPixelSize ItemPixelSize { get; }

    public nint ChromeWindowHandle { get; }

    public DesktopScreenSelectionRect ExtendedFrameBounds { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref softwareBitmap, null)?.Dispose();
    }
}

public sealed class WindowCaptureSnapshotException : InvalidOperationException
{
    public WindowCaptureSnapshotException(string stage, Exception innerException)
        : base(
            $"Snapshot capture failed at '{stage}': {innerException.GetType().Name}{GetFactoryOperationSuffix(innerException)} (HRESULT 0x{innerException.HResult:X8}).",
            innerException)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (stage.Trim().Length == 0)
        {
            throw new ArgumentException("A capture stage is required.", nameof(stage));
        }

        Stage = stage.Trim();
        ErrorHResult = innerException.HResult;
    }

    public string Stage { get; }

    public int ErrorHResult { get; }

    private static string GetFactoryOperationSuffix(Exception exception) =>
        exception is GraphicsCaptureItemFactoryException factoryException
            ? $" (operation {factoryException.Operation})"
            : string.Empty;
}

public static class WindowCaptureSnapshotService
{
    public static Task<WindowCaptureSnapshot> CaptureAsync(
        ChromeWindowInfo chromeWindow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chromeWindow);
        return CaptureAsync(
            chromeWindow.WindowHandle,
            chromeWindow.ExtendedFrameBounds,
            cancellationToken);
    }

    public static Task<WindowCaptureSnapshot> CaptureAsync(
        nint chromeWindowHandle,
        CancellationToken cancellationToken = default)
    {
        if (chromeWindowHandle == 0)
        {
            throw new ArgumentException("A Chrome window handle is required.", nameof(chromeWindowHandle));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var bounds = WindowGeometry.GetExtendedFrameBounds(chromeWindowHandle);
        return CaptureAsync(chromeWindowHandle, bounds, cancellationToken);
    }

    private static async Task<WindowCaptureSnapshot> CaptureAsync(
        nint chromeWindowHandle,
        DesktopScreenSelectionRect extendedFrameBounds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stage = "ValidateWindowGeometry";
        GraphicsCaptureItem? captureItem = null;
        CaptureItemPixelSize itemPixelSize = default;
        var completion = new TaskCompletionSource<Direct3D11CaptureFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var frameGuard = new SingleFrameCaptureGuard();
        Direct3D11CaptureFramePool? framePool = null;
        GraphicsCaptureSession? session = null;
        Direct3D11CaptureFrame? capturedFrame = null;
        SoftwareBitmap? copiedBitmap = null;

        void CompleteFromFrame(Direct3D11CaptureFramePool sender, object args)
        {
            try
            {
                var frame = sender.TryGetNextFrame();
                if (frame is null)
                {
                    return;
                }

                if (!frameGuard.TryClaimFrame() || !completion.TrySetResult(frame))
                {
                    frame.Dispose();
                }
            }
            catch (Exception exception)
            {
                frameGuard.TryCancel();
                completion.TrySetException(exception);
            }
        }

        void CompleteFromClosedItem(GraphicsCaptureItem sender, object args)
        {
            if (frameGuard.TryCancel())
            {
                completion.TrySetException(
                    new InvalidOperationException("The capture item closed before the snapshot was complete."));
            }
        }

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            frameGuard.TryCancel();
            completion.TrySetCanceled(cancellationToken);
        });

        try
        {
            WindowCaptureSnapshotContract.ValidateWindowGeometry(chromeWindowHandle, extendedFrameBounds);
            stage = "CreateGraphicsCaptureItem";
            captureItem = GraphicsCaptureItemFactory.CreateForWindow(chromeWindowHandle);
            itemPixelSize = new CaptureItemPixelSize(captureItem.Size.Width, captureItem.Size.Height);

            stage = "CreateDirect3DDevice";
            var direct3DDevice = CreateDirect3DDevice();
            stage = "CreateFramePool";
            framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                direct3DDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                1,
                captureItem.Size);
            stage = "CreateCaptureSession";
            session = framePool.CreateCaptureSession(captureItem);
            framePool.FrameArrived += CompleteFromFrame;
            captureItem.Closed += CompleteFromClosedItem;
            stage = "StartCapture";
            session.StartCapture();

            stage = "AwaitFrame";
            capturedFrame = await completion.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            using (capturedFrame)
            {
                stage = "ValidateFrameSize";
                var framePixelSize = new CaptureItemPixelSize(
                    capturedFrame.ContentSize.Width,
                    capturedFrame.ContentSize.Height);
                if (framePixelSize != itemPixelSize)
                {
                    throw new InvalidOperationException("The capture frame size did not match the capture item size.");
                }

                stage = "CopySoftwareBitmap";
                copiedBitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(capturedFrame.Surface)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);
            }

            stage = "ValidateBitmap";
            var bitmapPixelSize = new CaptureItemPixelSize(copiedBitmap.PixelWidth, copiedBitmap.PixelHeight);
            WindowCaptureSnapshotContract.ValidateMetadata(
                chromeWindowHandle,
                itemPixelSize,
                bitmapPixelSize,
                extendedFrameBounds);
            stage = "CompleteSnapshot";
            if (!frameGuard.TryComplete())
            {
                throw new InvalidOperationException("The capture snapshot became stale before completion.");
            }

            var snapshot = new WindowCaptureSnapshot(
                copiedBitmap,
                itemPixelSize,
                chromeWindowHandle,
                extendedFrameBounds);
            copiedBitmap = null;
            return snapshot;
        }
        catch (OperationCanceledException)
        {
            frameGuard.TryCancel();
            throw;
        }
        catch (WindowCaptureSnapshotException exception)
        {
            frameGuard.TryCancel();
            Debug.WriteLine(exception.ToString());
            throw;
        }
        catch (Exception exception)
        {
            frameGuard.TryCancel();
            Debug.WriteLine(exception.ToString());
            throw new WindowCaptureSnapshotException(stage, exception);
        }
        finally
        {
            if (captureItem is not null)
            {
                captureItem.Closed -= CompleteFromClosedItem;
            }
            if (framePool is not null)
            {
                framePool.FrameArrived -= CompleteFromFrame;
            }

            session?.Dispose();
            framePool?.Dispose();
            copiedBitmap?.Dispose();
        }
    }

    private static IDirect3DDevice CreateDirect3DDevice()
    {
        nint nativeDevice = 0;
        nint immediateContext = 0;
        try
        {
            var featureLevels = new[]
            {
                D3DFeatureLevel.Level11_0,
                D3DFeatureLevel.Level10_0
            };
            ThrowIfFailed(Direct3DNativeMethods.D3D11CreateDevice(
                0,
                D3DDriverType.Hardware,
                0,
                D3DCreateDeviceFlags.BgraSupport,
                featureLevels,
                (uint)featureLevels.Length,
                Direct3DNativeMethods.D3D11SdkVersion,
                out nativeDevice,
                out _,
                out immediateContext));

            var dxgiDeviceId = Direct3DNativeMethods.DxgiDeviceId;
            ThrowIfFailed(Marshal.QueryInterface(nativeDevice, in dxgiDeviceId, out var dxgiDevice));
            try
            {
                ThrowIfFailed(Direct3DNativeMethods.CreateDirect3D11DeviceFromDxgiDevice(
                    dxgiDevice,
                    out var graphicsDevice));
                try
                {
                    return MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevice);
                }
                finally
                {
                    Marshal.Release(graphicsDevice);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevice);
            }
        }
        finally
        {
            if (immediateContext != 0)
            {
                Marshal.Release(immediateContext);
            }

            if (nativeDevice != 0)
            {
                Marshal.Release(nativeDevice);
            }
        }
    }

    private static void ThrowIfFailed(int hresult)
    {
        if (hresult < 0)
        {
            Marshal.ThrowExceptionForHR(hresult);
        }
    }

    private enum D3DDriverType : uint
    {
        Hardware = 1
    }

    private enum D3DFeatureLevel : uint
    {
        Level10_0 = 0xA000,
        Level11_0 = 0xB000
    }

    private enum D3DCreateDeviceFlags : uint
    {
        BgraSupport = 0x20
    }

    private static class Direct3DNativeMethods
    {
        internal const uint D3D11SdkVersion = 7;
        internal static readonly Guid DxgiDeviceId = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

        [DllImport("d3d11.dll", ExactSpelling = true)]
        internal static extern int D3D11CreateDevice(
            nint adapter,
            D3DDriverType driverType,
            nint software,
            D3DCreateDeviceFlags flags,
            [In] D3DFeatureLevel[] featureLevels,
            uint featureLevelCount,
            uint sdkVersion,
            out nint device,
            out D3DFeatureLevel featureLevel,
            out nint immediateContext);

        [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", ExactSpelling = true)]
        internal static extern int CreateDirect3D11DeviceFromDxgiDevice(
            nint dxgiDevice,
            out nint graphicsDevice);
    }
}

using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Canvas;
using Translator.Core;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Globalization;
using Windows.Media.Ocr;
using WinRT;
using WinRT.Interop;
using CoreOcrResult = Translator.Core.OcrResult;

namespace Translator.Windows;

public sealed class WindowsCaptureOcrController : IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan MinimumSampleInterval = TimeSpan.FromMilliseconds(100);
    private const int FramePoolBufferCount = 2;

    private readonly object gate = new();
    private readonly OcrDocumentStabilitySelector stabilitySelector = new();
    private readonly OcrEngineCache ocrEngines = new();
    private readonly SelectionEpochGate selectionEpochs = new();
    private GraphicsCaptureItem? captureItem;
    private Direct3D11CaptureFramePool? framePool;
    private GraphicsCaptureSession? captureSession;
    // GetSharedDevice returns a shared Win2D device. This controller borrows the reference;
    // ownership and disposal remain with Win2D.
    private CanvasDevice? canvasDevice;
    private CancellationTokenSource? runCancellation;
    private CancellationTokenSource? startupCancellation;
    private TaskCompletionSource? startupCompletion;
    private LatestCaptureFramePump? framePump;
    private LatestOcrFrameScheduler<SoftwareBitmap>? frameScheduler;
    private Task? stopDrainTask;
    private Task? disposeTask;
    private long captureEpoch;
    private bool starting;
    private bool started;
    private bool disposed;
    private WindowCaptureSelection? currentSelection;
    private WindowCaptureSelectionInvalidation? lastSelectionInvalidation;

    public event Action<CoreOcrResult>? OcrResultPublished;

    public event Action<WindowCaptureSelectionInvalidation>? SelectionInvalidated;

    public WindowCaptureSelection? CurrentSelection
    {
        get
        {
            lock (gate)
            {
                return currentSelection;
            }
        }
    }

    public WindowCaptureSelectionInvalidation? LastSelectionInvalidation
    {
        get
        {
            lock (gate)
            {
                return lastSelectionInvalidation;
            }
        }
    }

    public async Task StartAsync(
        nint ownerHwnd,
        string sourceLanguage,
        CancellationToken cancellationToken = default)
    {
        if (ownerHwnd == 0)
        {
            throw new ArgumentException("An owner HWND is required.", nameof(ownerHwnd));
        }

        var languageTag = OcrLanguageCatalog.Map(sourceLanguage);
        CancellationTokenSource? startCancellation = null;
        TaskCompletionSource startCompletion = null!;
        long startEpoch;

        lock (gate)
        {
            ThrowIfDisposed();
            if (started || starting)
            {
                throw new InvalidOperationException("Capture is already starting or started.");
            }

            if (stopDrainTask is not null)
            {
                if (!stopDrainTask.IsCompleted)
                {
                    throw new InvalidOperationException("Capture is still stopping.");
                }

                stopDrainTask = null;
            }

            starting = true;
            startEpoch = selectionEpochs.BeginSelection();
            startCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupCancellation = startCancellation;
            startCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            startupCompletion = startCompletion;
        }

        GraphicsCaptureItem? selectedItem = null;
        Direct3D11CaptureFramePool? selectedFramePool = null;
        GraphicsCaptureSession? selectedSession = null;
        CanvasDevice? selectedCanvasDevice = null;

        try
        {
            var picker = new GraphicsCapturePicker();
            InitializeWithWindow.Initialize(picker, ownerHwnd);
            selectedItem = await picker.PickSingleItemAsync()
                .AsTask(startCancellation.Token)
                .ConfigureAwait(false);
            startCancellation.Token.ThrowIfCancellationRequested();

            if (selectedItem is null)
            {
                lock (gate)
                {
                    starting = false;
                    startupCancellation = null;
                }

                return;
            }

            _ = ocrEngines.Get(languageTag);
            selectedCanvasDevice = CanvasDevice.GetSharedDevice();
            var direct3DDevice = selectedCanvasDevice.As<IDirect3DDevice>();
            selectedFramePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                direct3DDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                FramePoolBufferCount,
                selectedItem.Size);
            selectedSession = selectedFramePool.CreateCaptureSession(selectedItem);

            lock (gate)
            {
                ThrowIfDisposed();
                startCancellation.Token.ThrowIfCancellationRequested();
                captureItem = selectedItem;
                framePool = selectedFramePool;
                captureSession = selectedSession;
                canvasDevice = selectedCanvasDevice;
                captureEpoch = startEpoch;
                currentSelection = null;
                lastSelectionInvalidation = null;
                var selectedRunCancellation = new CancellationTokenSource();
                var selectedScheduler = new LatestOcrFrameScheduler<SoftwareBitmap>(
                    (bitmap, epoch) => ProcessFrameAsync(bitmap, selectedRunCancellation.Token, epoch),
                    selectionEpochs.IsCurrent,
                    MinimumSampleInterval);
                var selectedPump = new LatestCaptureFramePump(
                    (frame, epoch) => CopyFrameAsync(frame, selectedRunCancellation.Token, epoch),
                    (bitmap, epoch, observedAt) => selectedScheduler.Submit(bitmap, epoch, observedAt),
                    selectionEpochs.IsCurrent);
                runCancellation = selectedRunCancellation;
                frameScheduler = selectedScheduler;
                framePump = selectedPump;
                currentLanguageTag = languageTag;
                selectedItem.Closed += OnCaptureItemClosed;
                selectedFramePool.FrameArrived += OnFrameArrived;
                stabilitySelector.Reset();
                started = true;
                starting = false;
                startupCancellation = null;
                selectedSession.StartCapture();
            }

            selectedItem = null;
            selectedFramePool = null;
            selectedSession = null;
            selectedCanvasDevice = null;
        }
        catch
        {
            if (selectedItem is not null)
            {
                selectedItem.Closed -= OnCaptureItemClosed;
            }

            CancellationTokenSource? failedRunCancellation = null;
            LatestCaptureFramePump? failedPump = null;
            LatestOcrFrameScheduler<SoftwareBitmap>? failedScheduler = null;

            lock (gate)
            {
                if (ReferenceEquals(captureItem, selectedItem))
                {
                    started = false;
                    captureItem = null;
                    framePool = null;
                    captureSession = null;
                    canvasDevice = null;
                    currentSelection = null;
                    captureEpoch = 0;
                    failedRunCancellation = runCancellation;
                    runCancellation = null;
                    failedPump = framePump;
                    framePump = null;
                    failedScheduler = frameScheduler;
                    frameScheduler = null;
                }

                starting = false;
                if (ReferenceEquals(startupCancellation, startCancellation))
                {
                    startupCancellation = null;
                }
            }

            failedRunCancellation?.Cancel();
            selectedSession?.Dispose();
            if (failedPump is not null)
            {
                await failedPump.StopAsync().ConfigureAwait(false);
            }

            if (failedScheduler is not null)
            {
                await failedScheduler.DisposeAsync().ConfigureAwait(false);
            }

            failedRunCancellation?.Dispose();
            selectedFramePool?.Dispose();

            throw;
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(startupCompletion, startCompletion))
                {
                    startupCompletion = null;
                }
            }

            startCompletion.TrySetResult();
            startCancellation.Dispose();
        }
    }

    public Task StartForWindowAsync(
        nint windowHandle,
        string sourceLanguage,
        ItemLocalCropRect itemLocalCrop,
        CancellationToken cancellationToken = default)
    {
        return StartForWindowAsync(
            windowHandle,
            sourceLanguage,
            itemLocalCrop,
            WindowGeometry.GetExtendedFrameBounds(windowHandle),
            cancellationToken);
    }

    public async Task StartForWindowAsync(
        nint windowHandle,
        string sourceLanguage,
        ItemLocalCropRect itemLocalCrop,
        DesktopScreenSelectionRect desktopScreenSelection,
        CancellationToken cancellationToken = default)
    {
        if (windowHandle == 0)
        {
            throw new ArgumentException("A window handle is required.", nameof(windowHandle));
        }

        var languageTag = OcrLanguageCatalog.Map(sourceLanguage);
        CancellationTokenSource? startCancellation = null;
        TaskCompletionSource startCompletion = null!;
        long startEpoch;

        lock (gate)
        {
            ThrowIfDisposed();
            if (started || starting)
            {
                throw new InvalidOperationException("Capture is already starting or started.");
            }

            if (stopDrainTask is not null)
            {
                if (!stopDrainTask.IsCompleted)
                {
                    throw new InvalidOperationException("Capture is still stopping.");
                }

                stopDrainTask = null;
            }

            starting = true;
            startEpoch = selectionEpochs.BeginSelection();
            startCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupCancellation = startCancellation;
            startCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            startupCompletion = startCompletion;
        }

        GraphicsCaptureItem? selectedItem = null;
        Direct3D11CaptureFramePool? selectedFramePool = null;
        GraphicsCaptureSession? selectedSession = null;
        CanvasDevice? selectedCanvasDevice = null;

        try
        {
            startCancellation.Token.ThrowIfCancellationRequested();
            selectedItem = GraphicsCaptureItemFactory.CreateForWindow(windowHandle);
            startCancellation.Token.ThrowIfCancellationRequested();
            var itemSize = new CaptureItemPixelSize(selectedItem.Size.Width, selectedItem.Size.Height);
            var selection = new WindowCaptureSelection(
                windowHandle,
                itemSize,
                CaptureCropContract.Validate(itemLocalCrop, itemSize),
                desktopScreenSelection,
                startEpoch);

            _ = ocrEngines.Get(languageTag);
            selectedCanvasDevice = CanvasDevice.GetSharedDevice();
            var direct3DDevice = selectedCanvasDevice.As<IDirect3DDevice>();
            selectedFramePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                direct3DDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                FramePoolBufferCount,
                selectedItem.Size);
            selectedSession = selectedFramePool.CreateCaptureSession(selectedItem);

            lock (gate)
            {
                ThrowIfDisposed();
                startCancellation.Token.ThrowIfCancellationRequested();
                captureItem = selectedItem;
                framePool = selectedFramePool;
                captureSession = selectedSession;
                canvasDevice = selectedCanvasDevice;
                captureEpoch = startEpoch;
                currentSelection = selection;
                lastSelectionInvalidation = null;
                var selectedRunCancellation = new CancellationTokenSource();
                var selectedScheduler = new LatestOcrFrameScheduler<SoftwareBitmap>(
                    (bitmap, epoch) => ProcessFrameAsync(bitmap, selectedRunCancellation.Token, epoch),
                    selectionEpochs.IsCurrent,
                    MinimumSampleInterval);
                var selectedPump = new LatestCaptureFramePump(
                    (frame, epoch) => CopyFrameAsync(frame, selectedRunCancellation.Token, epoch),
                    (bitmap, epoch, observedAt) => selectedScheduler.Submit(bitmap, epoch, observedAt),
                    selectionEpochs.IsCurrent);
                runCancellation = selectedRunCancellation;
                frameScheduler = selectedScheduler;
                framePump = selectedPump;
                currentLanguageTag = languageTag;
                selectedItem.Closed += OnCaptureItemClosed;
                selectedFramePool.FrameArrived += OnFrameArrived;
                stabilitySelector.Reset();
                started = true;
                starting = false;
                startupCancellation = null;
                selectedSession.StartCapture();
            }

            selectedItem = null;
            selectedFramePool = null;
            selectedSession = null;
            selectedCanvasDevice = null;
        }
        catch
        {
            if (selectedItem is not null)
            {
                selectedItem.Closed -= OnCaptureItemClosed;
            }

            CancellationTokenSource? failedRunCancellation = null;
            LatestCaptureFramePump? failedPump = null;
            LatestOcrFrameScheduler<SoftwareBitmap>? failedScheduler = null;

            lock (gate)
            {
                if (ReferenceEquals(captureItem, selectedItem))
                {
                    started = false;
                    captureItem = null;
                    framePool = null;
                    captureSession = null;
                    canvasDevice = null;
                    currentSelection = null;
                    captureEpoch = 0;
                    failedRunCancellation = runCancellation;
                    runCancellation = null;
                    failedPump = framePump;
                    framePump = null;
                    failedScheduler = frameScheduler;
                    frameScheduler = null;
                }

                starting = false;
                if (ReferenceEquals(startupCancellation, startCancellation))
                {
                    startupCancellation = null;
                }
            }

            failedRunCancellation?.Cancel();
            selectedSession?.Dispose();
            if (failedPump is not null)
            {
                await failedPump.StopAsync().ConfigureAwait(false);
            }

            if (failedScheduler is not null)
            {
                await failedScheduler.DisposeAsync().ConfigureAwait(false);
            }

            failedRunCancellation?.Dispose();
            selectedFramePool?.Dispose();

            throw;
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(startupCompletion, startCompletion))
                {
                    startupCompletion = null;
                }
            }

            startCompletion.TrySetResult();
            startCancellation.Dispose();
        }
    }

    public Task StopAsync()
    {
        lock (gate)
        {
            return GetStopDrainTaskLocked();
        }
    }

    private async Task StopCoreAsync()
    {
        GraphicsCaptureItem? item;
        Direct3D11CaptureFramePool? pool;
        GraphicsCaptureSession? session;
        CancellationTokenSource? cancellation;
        LatestCaptureFramePump? pump;
        LatestOcrFrameScheduler<SoftwareBitmap>? scheduler;
        Task? startup;

        lock (gate)
        {
            if (starting)
            {
                selectionEpochs.BeginSelection();
                startupCancellation?.Cancel();
                startup = startupCompletion?.Task;
                item = null;
                pool = null;
                session = null;
                cancellation = null;
                pump = null;
                scheduler = null;
            }
            else if (!started)
            {
                selectionEpochs.BeginSelection();
                return;
            }
            else
            {
                startup = null;
                started = false;
                selectionEpochs.BeginSelection();
                item = captureItem;
                pool = framePool;
                session = captureSession;
                cancellation = runCancellation;
                pump = framePump;
                scheduler = frameScheduler;
                captureItem = null;
                framePool = null;
                captureSession = null;
                canvasDevice = null;
                currentSelection = null;
                captureEpoch = 0;
                runCancellation = null;
                framePump = null;
                frameScheduler = null;

                if (item is not null)
                {
                    item.Closed -= OnCaptureItemClosed;
                }

                if (pool is not null)
                {
                    pool.FrameArrived -= OnFrameArrived;
                }

                cancellation?.Cancel();
            }
        }

        if (startup is not null)
        {
            await startup.ConfigureAwait(false);
            return;
        }

        session?.Dispose();

        if (pump is not null)
        {
            await pump.StopAsync().ConfigureAwait(false);
        }

        if (scheduler is not null)
        {
            await scheduler.DisposeAsync().ConfigureAwait(false);
        }

        pool?.Dispose();
        cancellation?.Dispose();
    }

    private Task GetStopDrainTaskLocked()
    {
        if (stopDrainTask is not null)
        {
            return stopDrainTask;
        }

        stopDrainTask = StopCoreAsync();
        return stopDrainTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposeTask is not null)
            {
                return new ValueTask(disposeTask);
            }

            disposed = true;
            var stop = GetStopDrainTaskLocked();
            disposeTask = DisposeCoreAsync(stop);
            return new ValueTask(disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task stop)
    {
        await stop.ConfigureAwait(false);
        ocrEngines.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Direct3D11CaptureFrame? frame = null;

        try
        {
            lock (gate)
            {
                if (!started || !ReferenceEquals(framePool, sender) || framePump is null)
                {
                    return;
                }

                frame = sender.TryGetNextFrame();
                if (frame is null)
                {
                    return;
                }
                var pump = framePump;
                var epoch = captureEpoch;
                var capturedFrame = frame;
                frame = null;
                pump.Submit(capturedFrame, epoch);
            }
        }
        catch
        {
        }
        finally
        {
            frame?.Dispose();
        }
    }

    private async Task<SoftwareBitmap?> CopyFrameAsync(
        Direct3D11CaptureFrame frame,
        CancellationToken cancellationToken,
        long epoch)
    {
        WindowCaptureSelection? selection;
        lock (gate)
        {
            if (!started || !selectionEpochs.IsCurrent(epoch))
            {
                return null;
            }

            selection = currentSelection;
        }

        var fullBitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        if (selection is null)
        {
            return fullBitmap;
        }

        using (fullBitmap)
        {
            var frameSize = new CaptureItemPixelSize(frame.ContentSize.Width, frame.ContentSize.Height);
            var bitmapSize = new CaptureItemPixelSize(fullBitmap.PixelWidth, fullBitmap.PixelHeight);
            if (!SoftwareBitmapCropContract.IsContentSizeCompatible(selection, frameSize) ||
                !SoftwareBitmapCropContract.IsContentSizeCompatible(selection, bitmapSize))
            {
                var invalidation = new WindowCaptureSelectionInvalidation(
                    selection.Epoch,
                    "The capture content size no longer matches the selected item crop.");
                _ = InvalidateSelectionAsync(invalidation);
                return null;
            }

            return await SoftwareBitmapCropper.CropAsync(
                    fullBitmap,
                    selection.ItemLocalCrop,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task ProcessFrameAsync(
        SoftwareBitmap bitmap,
        CancellationToken cancellationToken,
        long epoch)
    {
        try
        {
            await RecognizeAndPublishAsync(bitmap, cancellationToken, epoch)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // A closed item or a transient WGC/OCR frame failure ends this frame only.
        }
    }

    private async Task RecognizeAndPublishAsync(
        SoftwareBitmap bitmap,
        CancellationToken cancellationToken,
        long epoch)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var engine = ocrEngines.Get(CurrentLanguageTag());
        var result = await engine.RecognizeAsync(bitmap)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var document = OcrDocumentMapper.MapLines(
            result.Lines.Select(line => new OcrLineSnapshot(
                line.Words.Select(word => new OcrWordSnapshot(
                    word.Text,
                    ToPhysicalBounds(word.BoundingRect))))));
        Publish(document, bitmap, epoch);
    }

    private async Task InvalidateSelectionAsync(WindowCaptureSelectionInvalidation invalidation)
    {
        lock (gate)
        {
            if (!started ||
                currentSelection is null ||
                currentSelection.Epoch != invalidation.Epoch ||
                !selectionEpochs.IsCurrent(invalidation.Epoch))
            {
                return;
            }

            selectionEpochs.BeginSelection();
            lastSelectionInvalidation = invalidation;
        }

        try
        {
            SelectionInvalidated?.Invoke(invalidation);
        }
        catch
        {
            // Subscribers cannot change capture state.
        }

        await StopAsync().ConfigureAwait(false);
    }

    private void Publish(CoreOcrResult document, SoftwareBitmap ocrCrop, long epoch)
    {
        lock (gate)
        {
            if (!started || !selectionEpochs.IsCurrent(epoch))
            {
                return;
            }

            CoreOcrResult presentedDocument;
            try
            {
                presentedDocument = OcrLineAppearanceSampler.AttachHints(document, ocrCrop);
            }
            catch
            {
                // Appearance is a hint; a transient bitmap format/read issue
                // must not suppress an otherwise valid OCR publication.
                presentedDocument = document;
            }

            var stableDocument = stabilitySelector.Observe(presentedDocument, DateTimeOffset.UtcNow);
            if (stableDocument is null)
            {
                return;
            }

            try
            {
                OcrResultPublished?.Invoke(stableDocument);
            }
            catch
            {
                // Subscribers cannot change capture state.
            }
        }
    }

    private string CurrentLanguageTag()
    {
        lock (gate)
        {
            return currentLanguageTag;
        }
    }

    private string currentLanguageTag = "ja-JP";

    private static PhysicalPixelRect ToPhysicalBounds(global::Windows.Foundation.Rect bounds)
    {
        var left = checked((int)Math.Floor(bounds.X));
        var top = checked((int)Math.Floor(bounds.Y));
        var right = checked((int)Math.Ceiling(bounds.X + bounds.Width));
        var bottom = checked((int)Math.Ceiling(bounds.Y + bounds.Height));
        return new PhysicalPixelRect(left, top, checked(right - left), checked(bottom - top));
    }

    private void OnCaptureItemClosed(GraphicsCaptureItem sender, object args)
    {
        _ = StopAsync();
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsCaptureOcrController));
        }
    }

    private sealed class LatestCaptureFramePump
    {
        private readonly object gate = new();
        private readonly Func<Direct3D11CaptureFrame, long, Task<SoftwareBitmap?>> copyAsync;
        private readonly Action<SoftwareBitmap, long, DateTimeOffset> submit;
        private readonly Func<long, bool> isCurrentEpoch;
        private PendingCaptureFrame? pendingFrame;
        private Task? pumpTask;
        private bool scheduled;
        private bool stopped;

        public LatestCaptureFramePump(
            Func<Direct3D11CaptureFrame, long, Task<SoftwareBitmap?>> copyAsync,
            Action<SoftwareBitmap, long, DateTimeOffset> submit,
            Func<long, bool> isCurrentEpoch)
        {
            this.copyAsync = copyAsync ?? throw new ArgumentNullException(nameof(copyAsync));
            this.submit = submit ?? throw new ArgumentNullException(nameof(submit));
            this.isCurrentEpoch = isCurrentEpoch ?? throw new ArgumentNullException(nameof(isCurrentEpoch));
        }

        public void Submit(Direct3D11CaptureFrame frame, long epoch)
        {
            ArgumentNullException.ThrowIfNull(frame);
            PendingCaptureFrame? replaced = null;

            lock (gate)
            {
                if (stopped || !isCurrentEpoch(epoch))
                {
                    replaced = new PendingCaptureFrame(frame, epoch);
                }
                else
                {
                    replaced = pendingFrame;
                    pendingFrame = new PendingCaptureFrame(frame, epoch);
                    if (!scheduled)
                    {
                        scheduled = true;
                        pumpTask = Task.Run(PumpAsync);
                    }
                }
            }

            replaced?.Frame.Dispose();
        }

        public async Task StopAsync()
        {
            PendingCaptureFrame? pending;
            Task? task;

            lock (gate)
            {
                stopped = true;
                pending = pendingFrame;
                pendingFrame = null;
                task = pumpTask;
            }

            pending?.Frame.Dispose();
            if (task is not null)
            {
                await task.ConfigureAwait(false);
            }
        }

        private async Task PumpAsync()
        {
            while (true)
            {
                Direct3D11CaptureFrame frame;
                long epoch;

                lock (gate)
                {
                    var pending = pendingFrame;
                    if (pending is null)
                    {
                        scheduled = false;
                        pumpTask = null;
                        return;
                    }

                    pendingFrame = null;
                    frame = pending.Frame;
                    epoch = pending.Epoch;
                }

                SoftwareBitmap? bitmap = null;
                try
                {
                    bitmap = await copyAsync(frame, epoch).ConfigureAwait(false);
                }
                catch
                {
                    // A closed item or a transient WGC copy failure ends this frame only.
                }
                finally
                {
                    frame.Dispose();
                }

                if (bitmap is null)
                {
                    continue;
                }

                var accepted = false;
                lock (gate)
                {
                    accepted = !stopped && isCurrentEpoch(epoch);
                }

                if (!accepted)
                {
                    bitmap.Dispose();
                    continue;
                }

                try
                {
                    submit(bitmap, epoch, DateTimeOffset.UtcNow);
                    bitmap = null;
                }
                catch
                {
                    bitmap?.Dispose();
                }
            }
        }

        private sealed record PendingCaptureFrame(Direct3D11CaptureFrame Frame, long Epoch);
    }

    private sealed class OcrEngineCache : IDisposable
    {
        private readonly Dictionary<string, OcrEngine> engines = new(StringComparer.OrdinalIgnoreCase);
        private readonly object gate = new();
        private bool disposed;

        public OcrEngine Get(string languageTag)
        {
            lock (gate)
            {
                if (disposed)
                {
                    throw new ObjectDisposedException(nameof(OcrEngineCache));
                }

                if (engines.TryGetValue(languageTag, out var engine))
                {
                    return engine;
                }

                engine = OcrEngine.TryCreateFromLanguage(new Language(languageTag))
                    ?? throw new InvalidOperationException($"OCR language '{languageTag}' is unavailable.");
                engines.Add(languageTag, engine);
                return engine;
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                engines.Clear();
            }
        }
    }
}

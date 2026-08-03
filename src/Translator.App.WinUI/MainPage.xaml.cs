using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Translator.Core;
using Translator.Providers.OpenAICompatible;
using Translator.Windows;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Translator_App_WinUI;

public sealed partial class MainPage : Page, IAsyncDisposable
{
    private const string XiaomiEndpoint = "https://api.xiaomimimo.com/v1";
    private const string XiaomiModel = "mimo-v2.5";
    private static readonly TimeSpan TranslationTimeout = TimeSpan.FromSeconds(30);

    private readonly DispatcherQueue dispatcherQueue;
    private readonly DispatcherQueueTimer windowMonitorTimer;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly LatestValueHandoff<OcrDocumentHandoff> pendingOcrDocument = new();
    private readonly LatestValueHandoff<LinePresentationSnapshot> pendingPresentation = new();
    private readonly ITranslationMemory translationMemory = new TranslationMemoryCache();
    private readonly object sessionGate = new();
    private readonly object presentationGate = new();
    private WindowsCaptureOcrController? captureController;
    private ITextTranslator? textTranslator;
    private HttpClient? httpClient;
    private CancellationTokenSource? runCancellation;
    private CancellationTokenSource? selectionCancellation;
    private BoundedLineTranslationCoordinator? lineTranslationCoordinator;
    private RegionSelectorWindow? selectorWindow;
    private Task? startTask;
    private Task? stopDrainTask;
    private Task? disposalTask;
    private CaptureSelectionCoordinates? regionSelection;
    private ChromeWindowInfo? selectionWindow;
    private nint ownerHwnd;
    private readonly ImportedFontService importedFontService = new();
    private readonly Dictionary<string, string> fontFamiliesByDisplayName = new(StringComparer.Ordinal)
    {
        [OverlayFont.Default.Family] = OverlayFont.Default.Family
    };
    private long ocrDocumentGeneration;
    private long latestPresentationGeneration;
    private long latestPresentationRevision;
    private bool hasLatestPresentationVersion;
    private int ocrDispatcherCallbackArmed;
    private int presentationDispatcherCallbackArmed;
    private IReadOnlyList<TranslatedOverlayLine> currentOverlayLines = Array.Empty<TranslatedOverlayLine>();
    private LinePresentationSnapshot? currentPresentation;
    private WindowCaptureSelection? currentOverlaySelection;
    private TranslationOverlayWindow? overlayWindow;
    private ITranslationOverlaySurface? overlaySurface;

    public TranslatorViewModel ViewModel { get; } = new();

    public MainPage()
    {
        InitializeComponent();
        dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        windowMonitorTimer = dispatcherQueue.CreateTimer();
        windowMonitorTimer.Interval = TimeSpan.FromSeconds(1);
        windowMonitorTimer.Tick += CheckSelectedWindow;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void SetOwnerHwnd(nint hwnd) => ownerHwnd = hwnd;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TranslatorViewModel.SelectedFont) or
            nameof(TranslatorViewModel.AutoContrast) or
            nameof(TranslatorViewModel.TranslationFontSize))
        {
            RefreshOverlayTypography();
        }
    }

    private void RefreshOverlayTypography()
    {
        if (currentOverlaySelection is null || currentPresentation is null)
        {
            return;
        }

        var successful = currentPresentation.Lines
            .Where(line => line.State == LinePresentationState.Success &&
                           !string.IsNullOrWhiteSpace(line.TranslatedText))
            .ToArray();
        if (successful.Length > 0)
        {
            SetOverlayLines(CreateOverlayLines(successful, currentOverlaySelection));
        }
    }

    private async void ImportFont(object sender, RoutedEventArgs e)
    {
        if (ownerHwnd == 0)
        {
            ViewModel.FontStatus = "Font import is available after the app window is ready.";
            return;
        }

        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".ttf");
        picker.FileTypeFilter.Add(".otf");
        InitializeWithWindow.Initialize(picker, ownerHwnd);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        var imported = await importedFontService.ImportAsync(file);
        if (imported is null)
        {
            ViewModel.FontStatus = "Choose a .ttf or .otf file.";
            return;
        }

        foreach (var family in imported.Families)
        {
            fontFamiliesByDisplayName[family.FamilyName] = family.FontFamily;
            if (!ViewModel.FontChoices.Contains(family.FamilyName))
            {
                ViewModel.FontChoices.Add(family.FamilyName);
            }
        }

        ViewModel.SelectedFont = imported.DisplayFamily;
        ViewModel.FontStatus = $"Saved {imported.FileName} to app storage. {imported.DisplayFamily} is active for translated overlays.";
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => RefreshChromeWindows(sender, e);

    private void RefreshChromeWindows(object sender, RoutedEventArgs e)
    {
        var selectedHandle = ViewModel.SelectedChromeWindow?.WindowHandle;
        ViewModel.ChromeWindows.Clear();
        foreach (var window in ChromeWindowEnumerator.EnumerateVisibleTopLevel())
        {
            ViewModel.ChromeWindows.Add(window);
        }

        ViewModel.SelectedChromeWindow = ViewModel.ChromeWindows.FirstOrDefault(window => window.WindowHandle == selectedHandle)
            ?? ViewModel.ChromeWindows.FirstOrDefault();
        UpdateRegionButtonState();
    }

    private async void WindowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await StopCoreAsync();
        regionSelection = null;
        selectionWindow = null;
        var selected = ViewModel.SelectedChromeWindow;
        ViewModel.RegionStatus = selected is null ? "Bounded" : "Window selected";
        ViewModel.RegionTitle = selected is null ? "No window selected" : selected.Title;
        ViewModel.RegionDetail = selected is null
            ? "Select a visible Chrome window to begin."
            : "Choose a frozen snapshot region before starting OCR.";
        UpdateRegionButtonState();
    }

    private async void ChooseRegion(object sender, RoutedEventArgs e)
    {
        var window = ViewModel.SelectedChromeWindow;
        if (window is null)
        {
            ViewModel.Status = "Select a visible Chrome window first.";
            return;
        }

        await StopCoreAsync();
        selectionCancellation?.Dispose();
        selectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
        ViewModel.Status = "Capturing one frozen Chrome frame…";
        ChooseRegionButton.IsEnabled = false;

        try
        {
            using var snapshot = await WindowCaptureSnapshotService.CaptureAsync(window, selectionCancellation.Token);
            selectorWindow = new RegionSelectorWindow(snapshot);
            selectorWindow.Activate();
            var coordinates = await selectorWindow.Completion;
            selectorWindow = null;

            if (coordinates is null)
            {
                ViewModel.Status = "Region selection canceled.";
                ViewModel.RegionStatus = "Not selected";
                ViewModel.RegionTitle = window.Title;
                ViewModel.RegionDetail = "Drag a region on a new snapshot to start OCR.";
                return;
            }

            regionSelection = coordinates;
            selectionWindow = window;
            ViewModel.RegionStatus = "Ready";
            ViewModel.RegionTitle = window.Title;
            ViewModel.RegionDetail = "A bounded region is ready; start the session to begin OCR.";
            ViewModel.Status = "Region selected. Ready to start.";
        }
        catch (RegionSelectorPreviewException exception)
        {
            ViewModel.Status =
                $"Error: Region preview failed at '{exception.Stage}': {exception.ErrorType} (HRESULT 0x{exception.ErrorHResult:X8}).";
        }
        catch (OperationCanceledException)
        {
            ViewModel.Status = "Region selection canceled.";
        }
        catch (Exception exception)
        {
            ViewModel.Status = ActionableError(exception);
        }
        finally
        {
            selectorWindow = null;
            selectionCancellation?.Dispose();
            selectionCancellation = null;
            UpdateRegionButtonState();
        }
    }

    private async void StartSession(object sender, RoutedEventArgs e)
    {
        lock (sessionGate)
        {
            if (startTask is not null || stopDrainTask is not null)
            {
                return;
            }
        }

        var selectedWindow = ViewModel.SelectedChromeWindow;
        if (selectedWindow is null || regionSelection is null || selectionWindow?.WindowHandle != selectedWindow.WindowHandle)
        {
            ViewModel.Status = "Select a Chrome window and choose a drag region first.";
            return;
        }

        Task? sessionStartTask = null;
        try
        {
            var endpoint = EndpointBox.Text.Trim();
            var model = ModelBox.Text.Trim();
            var profile = IsXiaomiProfile(endpoint, model)
                ? OpenAICompatibleRequestProfile.XiaomiMiMo
                : OpenAICompatibleRequestProfile.Standard;
            var options = new OpenAICompatibleOptions(
                endpoint,
                model,
                model,
                string.IsNullOrWhiteSpace(ApiKeyBox.Password) ? null : ApiKeyBox.Password,
                profile);

            if (ownerHwnd == 0)
            {
                throw new InvalidOperationException("The application window handle is not available.");
            }

            ViewModel.Status = "Starting OCR for the selected Chrome region…";
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            var sourceTag = SourceLanguageTag(ViewModel.SourceLanguage);
            var coordinates = regionSelection.Value;
            lock (sessionGate)
            {
                if (startTask is not null || stopDrainTask is not null)
                {
                    return;
                }

                ChooseRegionButton.IsEnabled = false;
                EnsureOverlayWindow(selectedWindow.WindowHandle);
                runCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
                httpClient = new HttpClient { Timeout = TranslationTimeout };
                textTranslator = new OpenAICompatibleTextTranslator(httpClient, options);
                lineTranslationCoordinator = new BoundedLineTranslationCoordinator(
                    textTranslator,
                    translationMemory,
                    maxConcurrency: 3);
                lineTranslationCoordinator.PresentationPublished += OnPresentationPublished;
                captureController = new WindowsCaptureOcrController();
                captureController.OcrResultPublished += OnOcrResultPublished;
                captureController.SelectionInvalidated += OnSelectionInvalidated;
                sessionStartTask = captureController.StartForWindowAsync(
                    selectedWindow.WindowHandle,
                    sourceTag,
                    coordinates.ItemLocalCrop,
                    coordinates.DesktopScreenSelection,
                    runCancellation.Token);
                startTask = sessionStartTask;
            }

            await sessionStartTask!;
            windowMonitorTimer.Start();
            ViewModel.RegionStatus = "Selected";
            ViewModel.RegionTitle = selectedWindow.Title;
            ViewModel.RegionDetail = "OCR is reading the confirmed bounded region.";
            ViewModel.Status = "Session running";
        }
        catch (OperationCanceledException)
        {
            if (!lifetimeCancellation.IsCancellationRequested)
            {
                ViewModel.Status = "Session stopped.";
            }
        }
        catch (Exception exception)
        {
            var error = ActionableError(exception);
            await StopCoreAsync();
            ViewModel.Status = error;
        }
        finally
        {
            bool ownsStartTask;
            lock (sessionGate)
            {
                ownsStartTask = ReferenceEquals(startTask, sessionStartTask);
                if (ownsStartTask)
                {
                    startTask = null;
                }
            }

            if (ownsStartTask &&
                (ViewModel.Status == "Session stopped." || ViewModel.Status.StartsWith("Error", StringComparison.Ordinal)))
            {
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;
                UpdateRegionButtonState();
            }
        }
    }

    private async void StopSession(object sender, RoutedEventArgs e) => await StopCoreAsync();

    private async void OnUnloaded(object sender, RoutedEventArgs e) => await StopCoreAsync();

    private void OnOcrResultPublished(OcrResult result)
    {
        var generation = Interlocked.Increment(ref ocrDocumentGeneration);
        var selection = captureController?.CurrentSelection;
        pendingOcrDocument.Publish(new OcrDocumentHandoff(result, generation, selection));
        ArmOcrDocumentDispatcher();
    }

    private void ArmOcrDocumentDispatcher()
    {
        if (Interlocked.CompareExchange(ref ocrDispatcherCallbackArmed, 1, 0) != 0)
        {
            return;
        }

        if (!dispatcherQueue.TryEnqueue(DrainLatestOcrDocument))
        {
            Interlocked.Exchange(ref ocrDispatcherCallbackArmed, 0);
        }
    }

    private void DrainLatestOcrDocument()
    {
        try
        {
            if (pendingOcrDocument.TryTake(out var handoff))
            {
                HandleOcrDocument(handoff.Document, handoff.Generation, handoff.Selection);
            }
        }
        finally
        {
            Interlocked.Exchange(ref ocrDispatcherCallbackArmed, 0);
            if (pendingOcrDocument.HasValue)
            {
                ArmOcrDocumentDispatcher();
            }
        }
    }

    private void HandleOcrDocument(
        OcrResult document,
        long generation,
        WindowCaptureSelection? selection)
    {
        if (generation != Volatile.Read(ref ocrDocumentGeneration))
        {
            return;
        }

        if (textTranslator is null ||
            runCancellation is null ||
            runCancellation.IsCancellationRequested ||
            lineTranslationCoordinator is null)
        {
            return;
        }

        var source = string.Join(Environment.NewLine, document.Text.Select(text => text.Text.Value));
        ViewModel.SourceText = string.IsNullOrWhiteSpace(source)
            ? "OCR output will appear here."
            : source;
        ViewModel.SourceState = string.IsNullOrWhiteSpace(source)
            ? "No text detected"
            : "Current OCR text";
        ViewModel.TranslationText = string.Empty;
        ViewModel.TranslationState = string.IsNullOrWhiteSpace(source)
            ? "Waiting for OCR"
            : "Translating lines…";
        currentPresentation = null;

        if (string.IsNullOrWhiteSpace(source))
        {
            lineTranslationCoordinator.Clear(generation);
            return;
        }

        LineTranslationRequest[] requests;
        try
        {
            var languagePair = new LanguagePair(ViewModel.SourceLanguage, ViewModel.TargetLanguage);
            var providerRevision = ModelBox.Text.Trim();
            requests = document.Text
                .Select(line => new LineTranslationRequest(
                    line,
                    new TranslationRequest(line.Text, languagePair, providerRevision)))
                .ToArray();
        }
        catch (Exception exception)
        {
            ViewModel.TranslationText = string.Empty;
            ViewModel.TranslationState = ActionableError(exception);
            return;
        }
        if (requests.Length == 0)
        {
            lineTranslationCoordinator.Clear(generation);
            return;
        }

        currentOverlaySelection = selection;
        lineTranslationCoordinator.Reconcile(generation, requests);
    }

    private void OnPresentationPublished(LinePresentationSnapshot snapshot)
    {
        lock (presentationGate)
        {
            if (snapshot.Generation != Volatile.Read(ref ocrDocumentGeneration) ||
                (hasLatestPresentationVersion &&
                 !PresentationSnapshotOrdering.IsNewer(
                     snapshot,
                     latestPresentationGeneration,
                     latestPresentationRevision)))
            {
                return;
            }

            latestPresentationGeneration = snapshot.Generation;
            latestPresentationRevision = snapshot.Revision;
            hasLatestPresentationVersion = true;
            pendingPresentation.Publish(snapshot);
        }

        ArmPresentationDispatcher();
    }

    private void ArmPresentationDispatcher()
    {
        if (Interlocked.CompareExchange(ref presentationDispatcherCallbackArmed, 1, 0) != 0)
        {
            return;
        }

        if (!dispatcherQueue.TryEnqueue(DrainLatestPresentation))
        {
            Interlocked.Exchange(ref presentationDispatcherCallbackArmed, 0);
        }
    }

    private void DrainLatestPresentation()
    {
        try
        {
            if (pendingPresentation.TryTake(out var snapshot))
            {
                var shouldApply = false;
                lock (presentationGate)
                {
                    shouldApply = snapshot.Generation == Volatile.Read(ref ocrDocumentGeneration) &&
                                  hasLatestPresentationVersion &&
                                  snapshot.Generation == latestPresentationGeneration &&
                                  snapshot.Revision == latestPresentationRevision;
                }

                if (shouldApply)
                {
                    ApplyPresentation(snapshot);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref presentationDispatcherCallbackArmed, 0);
            if (pendingPresentation.HasValue)
            {
                ArmPresentationDispatcher();
            }
        }
    }

    private void ApplyPresentation(LinePresentationSnapshot snapshot)
    {
        if (snapshot.IsClear)
        {
            currentPresentation = null;
            ClearOverlay();
            return;
        }

        currentPresentation = snapshot;
        ApplyTranslationPanelState(snapshot);

        if (currentOverlaySelection is null)
        {
            return;
        }

        var successful = snapshot.Lines
            .Where(line => line.State == LinePresentationState.Success &&
                           !string.IsNullOrWhiteSpace(line.TranslatedText))
            .ToArray();
        SetOverlayLines(CreateOverlayLines(successful, currentOverlaySelection));
    }

    private void OnSelectionInvalidated(WindowCaptureSelectionInvalidation invalidation)
    {
        var generation = Interlocked.Increment(ref ocrDocumentGeneration);
        lineTranslationCoordinator?.Clear(generation);
        dispatcherQueue.TryEnqueue(() => _ = HandleSelectionInvalidatedAsync(invalidation));
    }

    private void CheckSelectedWindow(DispatcherQueueTimer sender, object args)
    {
        var selected = selectionWindow;
        if (selected is null || captureController is null)
        {
            return;
        }

        try
        {
            if (!ChromeWindowEnumerator.EnumerateVisibleTopLevel().Any(window => window.WindowHandle == selected.WindowHandle))
            {
                windowMonitorTimer.Stop();
                var epoch = captureController.CurrentSelection?.Epoch ?? 1;
                _ = HandleSelectionInvalidatedAsync(new WindowCaptureSelectionInvalidation(
                    epoch,
                    "The selected Chrome window is no longer available."));
            }
        }
        catch
        {
            // Window enumeration is best effort; capture remains authoritative.
        }
    }

    private async Task HandleSelectionInvalidatedAsync(WindowCaptureSelectionInvalidation invalidation)
    {
        ClearOverlay();
        regionSelection = null;
        selectionWindow = null;
        await StopCoreAsync();
        ViewModel.RegionStatus = "Invalid";
        ViewModel.RegionTitle = "Region invalidated";
        ViewModel.RegionDetail = $"{invalidation.Reason} Reselect a region before starting again.";
        ViewModel.Status = "Selection invalidated; reselect the region.";
        UpdateRegionButtonState();
    }

    private void EnsureOverlayWindow(nint chromeWindowHandle)
    {
        if (overlaySurface is not null)
        {
            return;
        }

        overlayWindow = new TranslationOverlayWindow(chromeWindowHandle);
        overlayWindow.Activate();
        overlaySurface = overlayWindow;
        overlaySurface.Hide();
    }

    private void ApplyTranslationPanelState(
        LinePresentationSnapshot snapshot)
    {
        ViewModel.TranslationText = string.Join(
            Environment.NewLine,
            snapshot.Lines.Select(line => line.State == LinePresentationState.Success &&
                                          !string.IsNullOrWhiteSpace(line.TranslatedText)
                ? line.TranslatedText
                : line.State == LinePresentationState.Pending
                ? "[Translation pending]"
                : "[Translation unavailable]"));
        var successfulCount = snapshot.Lines.Count(line =>
            line.State == LinePresentationState.Success &&
            !string.IsNullOrWhiteSpace(line.TranslatedText));
        ViewModel.TranslationState = !snapshot.IsComplete
            ? "Translating lines…"
            : successfulCount == snapshot.Lines.Count
            ? "Current translation"
            : $"Translated {successfulCount} of {snapshot.Lines.Count} lines";
    }

    private IReadOnlyList<TranslatedOverlayLine> CreateOverlayLines(
        IEnumerable<LinePresentationLine> results,
        WindowCaptureSelection selection)
    {
        var overlayLines = new List<TranslatedOverlayLine>();
        foreach (var result in results)
        {
            try
            {
                var bounds = OcrLineOverlayProjector.ProjectToDesktop(result.SourceLine, selection);
                overlayLines.Add(new TranslatedOverlayLine(
                    result.OccurrenceId,
                    new OverlayDesktopBounds(bounds.Left, bounds.Top, bounds.Width, bounds.Height),
                    result.TranslatedText!,
                    CreateOverlayAppearance(result.SourceLine),
                    CreateOverlayFont()));
            }
            catch (ArgumentOutOfRangeException)
            {
                // A stale line cannot be placed against a newer selection.
            }
        }

        return overlayLines;
    }

    private void SetOverlayLines(IEnumerable<TranslatedOverlayLine> lines)
    {
        var nextLines = lines.ToArray();
        if (currentOverlayLines.SequenceEqual(nextLines))
        {
            return;
        }

        currentOverlayLines = nextLines;
        if (overlaySurface is null)
        {
            return;
        }

        if (currentOverlayLines.Count == 0)
        {
            overlaySurface.Clear();
            overlaySurface.Hide();
            return;
        }

        overlaySurface.UpdateLines(currentOverlayLines);
        overlaySurface.Show();
    }

    private void ClearOverlay()
    {
        currentOverlayLines = Array.Empty<TranslatedOverlayLine>();
        currentPresentation = null;
        currentOverlaySelection = null;
        if (overlaySurface is null)
        {
            return;
        }

        overlaySurface.Clear();
        overlaySurface.Hide();
    }

    private OverlayAppearance CreateOverlayAppearance(OcrText line)
    {
        var tone = ViewModel.AutoContrast && line.AppearanceHint is { } appearance
            ? OcrContrastSelector.Select(appearance)
            : OverlayForegroundTone.Dark;
        var foreground = tone == OverlayForegroundTone.Light
            ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
            : Windows.UI.Color.FromArgb(255, 0, 0, 0);
        var background = tone == OverlayForegroundTone.Light
            ? Windows.UI.Color.FromArgb(210, 0, 0, 0)
            : Windows.UI.Color.FromArgb(210, 255, 255, 255);
        return new OverlayAppearance(foreground, background);
    }

    private OverlayFont CreateOverlayFont()
    {
        var family = fontFamiliesByDisplayName.TryGetValue(ViewModel.SelectedFont, out var importedFamily) &&
                     !string.IsNullOrWhiteSpace(importedFamily)
            ? importedFamily
            : OverlayFont.Default.Family;
        return new OverlayFont(family, Math.Max(10, ViewModel.TranslationFontSize));
    }

    private async Task StopCoreAsync()
    {
        Task drain;
        lock (sessionGate)
        {
            if (stopDrainTask is null)
            {
                var resources = CaptureSessionResourcesLocked();
                stopDrainTask = DrainSessionAsync(resources);
            }

            drain = stopDrainTask;
        }

        try
        {
            await drain;
        }
        finally
        {
            lock (sessionGate)
            {
                if (ReferenceEquals(stopDrainTask, drain))
                {
                    stopDrainTask = null;
                }
            }
        }
    }

    private StopSessionResources CaptureSessionResourcesLocked()
    {
        Interlocked.Increment(ref ocrDocumentGeneration);
        windowMonitorTimer.Stop();
        selectionCancellation?.Cancel();
        selectorWindow?.Cancel();
        selectorWindow = null;
        runCancellation?.Cancel();

        var resources = new StopSessionResources(
            startTask,
            captureController,
            lineTranslationCoordinator,
            httpClient,
            runCancellation,
            selectionCancellation,
            overlaySurface);

        if (resources.CaptureController is not null)
        {
            resources.CaptureController.OcrResultPublished -= OnOcrResultPublished;
            resources.CaptureController.SelectionInvalidated -= OnSelectionInvalidated;
        }

        if (resources.Coordinator is not null)
        {
            resources.Coordinator.PresentationPublished -= OnPresentationPublished;
        }

        captureController = null;
        lineTranslationCoordinator = null;
        textTranslator = null;
        httpClient = null;
        runCancellation = null;
        selectionCancellation = null;
        startTask = null;
        overlaySurface = null;
        overlayWindow = null;
        currentOverlayLines = Array.Empty<TranslatedOverlayLine>();
        currentPresentation = null;
        currentOverlaySelection = null;
        regionSelection = null;
        selectionWindow = null;
        StartButton.IsEnabled = false;
        return resources;
    }

    private async Task DrainSessionAsync(StopSessionResources resources)
    {
        try
        {
            if (resources.StartTask is not null)
            {
                try
                {
                    await resources.StartTask;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    ViewModel.Status = ActionableError(exception);
                }
            }

            if (resources.CaptureController is not null)
            {
                try
                {
                    await resources.CaptureController.DisposeAsync();
                }
                catch
                {
                }
            }

            if (resources.Coordinator is not null)
            {
                try
                {
                    await resources.Coordinator.DisposeAsync();
                }
                catch
                {
                }
            }
        }
        finally
        {
            resources.HttpClient?.Dispose();
            resources.RunCancellation?.Dispose();
            resources.SelectionCancellation?.Dispose();

            if (resources.OverlaySurface is not null)
            {
                try
                {
                    resources.OverlaySurface.Clear();
                    resources.OverlaySurface.Hide();
                }
                catch
                {
                }

                try
                {
                    resources.OverlaySurface.Dispose();
                }
                catch
                {
                }
            }

            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            ViewModel.RegionStatus = "Bounded";
            ViewModel.RegionTitle = "No region selected";
            ViewModel.RegionDetail = "Select a Chrome window and choose a drag region.";
            ViewModel.Status = "Session stopped.";
            UpdateRegionButtonState();
        }
    }

    private sealed record StopSessionResources(
        Task? StartTask,
        WindowsCaptureOcrController? CaptureController,
        BoundedLineTranslationCoordinator? Coordinator,
        HttpClient? HttpClient,
        CancellationTokenSource? RunCancellation,
        CancellationTokenSource? SelectionCancellation,
        ITranslationOverlaySurface? OverlaySurface);

    private sealed record OcrDocumentHandoff(
        OcrResult Document,
        long Generation,
        WindowCaptureSelection? Selection);

    public ValueTask DisposeAsync()
    {
        lock (sessionGate)
        {
            disposalTask ??= DisposeCoreAsync();
            return new ValueTask(disposalTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        lifetimeCancellation.Cancel();
        await StopCoreAsync();
        lifetimeCancellation.Dispose();
    }

    private void UpdateRegionButtonState()
    {
        ChooseRegionButton.IsEnabled = ViewModel.SelectedChromeWindow is not null && startTask is null;
    }

    private static string SourceLanguageTag(string language) => language switch
    {
        "Japanese" => "ja-JP",
        "Chinese Simplified" => "zh-Hans",
        "Chinese Traditional" => "zh-Hant",
        _ => throw new ArgumentException("Select a supported source language.", nameof(language))
    };

    private static bool IsXiaomiProfile(string endpoint, string model) =>
        string.Equals(endpoint, XiaomiEndpoint, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(model, XiaomiModel, StringComparison.OrdinalIgnoreCase);

    private static string ActionableError(Exception exception) => exception switch
    {
        ArgumentException => $"Error: {exception.Message} Check the endpoint, model, and language settings.",
        HttpRequestException => $"Error: {exception.Message}",
        _ => $"Error: {exception.Message}"
    };
}

public sealed class TranslatorViewModel : INotifyPropertyChanged
{
    public ObservableCollection<string> SourceLanguages { get; } = ["Japanese", "Chinese Simplified", "Chinese Traditional"];
    public ObservableCollection<string> TargetLanguages { get; } = ["English", "Vietnamese"];
    public ObservableCollection<ChromeWindowInfo> ChromeWindows { get; } = [];
    public ObservableCollection<string> FontChoices { get; } = ["Segoe UI Variable"];

    private string sourceLanguage = "Japanese";
    private string targetLanguage = "English";
    private ChromeWindowInfo? selectedChromeWindow;
    private string status = "Ready to start";
    private string regionStatus = "Bounded";
    private string regionTitle = "No window selected";
    private string regionDetail = "Select a visible Chrome window to begin.";
    private string sourceText = "OCR output will appear here.";
    private string translationText = "Translated text will appear here.";
    private string sourceState = "Waiting for capture";
    private string translationState = "Waiting for OCR";
    private string selectedFont = "Segoe UI Variable";
    private bool autoContrast = true;
    private double translationFontSize = 16;
    private string fontStatus = "Segoe UI Variable · default and ready";

    public string SourceLanguage { get => sourceLanguage; set => SetField(ref sourceLanguage, value); }
    public string TargetLanguage { get => targetLanguage; set => SetField(ref targetLanguage, value); }
    public ChromeWindowInfo? SelectedChromeWindow { get => selectedChromeWindow; set => SetField(ref selectedChromeWindow, value); }
    public string Status { get => status; set => SetField(ref status, value); }
    public string RegionStatus { get => regionStatus; set => SetField(ref regionStatus, value); }
    public string RegionTitle { get => regionTitle; set => SetField(ref regionTitle, value); }
    public string RegionDetail { get => regionDetail; set => SetField(ref regionDetail, value); }
    public string SourceText { get => sourceText; set => SetField(ref sourceText, value); }
    public string TranslationText { get => translationText; set => SetField(ref translationText, value); }
    public string SourceState { get => sourceState; set => SetField(ref sourceState, value); }
    public string TranslationState { get => translationState; set => SetField(ref translationState, value); }
    public string SelectedFont { get => selectedFont; set => SetField(ref selectedFont, value); }
    public bool AutoContrast { get => autoContrast; set => SetField(ref autoContrast, value); }
    public double TranslationFontSize { get => translationFontSize; set => SetField(ref translationFontSize, value); }
    public string FontStatus { get => fontStatus; set => SetField(ref fontStatus, value); }
    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics;
using System.Threading.Tasks;
using Translator.Windows;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.System;

namespace Translator_App_WinUI;

public sealed partial class RegionSelectorWindow : Window
{
    private readonly WindowCaptureSnapshot snapshot;
    private readonly TaskCompletionSource<CaptureSelectionCoordinates?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Point? dragStart;
    private DipRect? currentSelection;
    private SoftwareBitmap? convertedDisplayBitmap;
    private bool pointerCaptured;
    private bool completed;
    private double displayedImageLeft;
    private double displayedImageTop;

    public RegionSelectorWindow(WindowCaptureSnapshot snapshot)
    {
        this.snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        InitializeComponent();
        Closed += OnClosed;
    }

    public Task<CaptureSelectionCoordinates?> Completion => completion.Task;

    public void Cancel()
    {
        Complete(null);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var source = new SoftwareBitmapSource();
            var originalBitmap = snapshot.SoftwareBitmap;
            if (originalBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
                originalBitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
            {
                convertedDisplayBitmap = SoftwareBitmap.Convert(
                    originalBitmap,
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied);
            }

            await source.SetBitmapAsync(convertedDisplayBitmap ?? originalBitmap);
            PreviewImage.Source = source;
            UpdateDisplayedImageLayout();
            RootGrid.Focus(FocusState.Programmatic);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception.ToString());
            SelectionStatus.Text = $"Could not display the snapshot: {exception.Message}";
            CompletePreviewFailure(exception);
        }
    }

    private void SelectionPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(SelectionCanvas).Properties.IsLeftButtonPressed)
        {
            return;
        }

        dragStart = ClampToImage(e.GetCurrentPoint(SelectionCanvas).Position);
        currentSelection = null;
        SelectionRectangle.Visibility = Visibility.Visible;
        pointerCaptured = SelectionCanvas.CapturePointer(e.Pointer);
        if (!pointerCaptured)
        {
            dragStart = null;
            SelectionRectangle.Visibility = Visibility.Collapsed;
            SelectionStatus.Text = "Could not capture pointer input.";
            return;
        }

        UpdateSelection(dragStart.Value);
    }

    private void SelectionMoved(object sender, PointerRoutedEventArgs e)
    {
        if (dragStart is null || !e.GetCurrentPoint(SelectionCanvas).Properties.IsLeftButtonPressed)
        {
            return;
        }

        UpdateSelection(ClampToImage(e.GetCurrentPoint(SelectionCanvas).Position));
    }

    private void SelectionReleased(object sender, PointerRoutedEventArgs e)
    {
        if (dragStart is null)
        {
            return;
        }

        UpdateSelection(ClampToImage(e.GetCurrentPoint(SelectionCanvas).Position));
        FinishPointerDrag(e.Pointer);
        if (currentSelection is not null)
        {
            SelectionStatus.Text = $"Selected {Math.Round(currentSelection.Value.Width):0} × {Math.Round(currentSelection.Value.Height):0} DIP. Confirm when ready.";
        }
    }

    private void UpdateSelection(Point end)
    {
        if (dragStart is null)
        {
            return;
        }

        var start = dragStart.Value;
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var right = Math.Max(start.X, end.X);
        var bottom = Math.Max(start.Y, end.Y);
        if (right <= left || bottom <= top)
        {
            currentSelection = null;
            SelectionRectangle.Visibility = Visibility.Collapsed;
            return;
        }

        currentSelection = new DipRect(left, top, right - left, bottom - top);
        RenderSelectionRectangle();
        SelectionStatus.Text = $"Selecting {Math.Round(right - left):0} × {Math.Round(bottom - top):0} DIP";
    }

    private void SelectionCanceled(object sender, PointerRoutedEventArgs e)
    {
        CancelPointerDrag("Selection canceled.");
    }

    private void SelectionCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        pointerCaptured = false;
        dragStart = null;
        if (currentSelection is not null)
        {
            SelectionStatus.Text = $"Selected {Math.Round(currentSelection.Value.Width):0} × {Math.Round(currentSelection.Value.Height):0} DIP. Confirm when ready.";
        }
    }

    private void ConfirmSelection(object sender, RoutedEventArgs e)
    {
        if (currentSelection is null)
        {
            SelectionStatus.Text = "Drag a region first.";
            return;
        }

        try
        {
            var imageSize = DisplayedImageSize();
            var coordinates = CaptureCoordinateTransform.MapImageSelection(
                currentSelection.Value,
                imageSize,
                snapshot.ItemPixelSize,
                snapshot.ExtendedFrameBounds);
            if (coordinates.ItemLocalCrop.Width < 8 || coordinates.ItemLocalCrop.Height < 8)
            {
                SelectionStatus.Text = "Select a larger region.";
                return;
            }

            Complete(coordinates);
        }
        catch (ArgumentOutOfRangeException)
        {
            SelectionStatus.Text = "The selection was outside the snapshot. Drag again.";
        }
    }

    private void CancelSelection(object sender, RoutedEventArgs e) => Complete(null);

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Complete(null);
        }
    }

    private Point ClampToImage(Point point)
    {
        var size = DisplayedImageSize();
        return new Point(Math.Clamp(point.X, 0, size.Width), Math.Clamp(point.Y, 0, size.Height));
    }

    private void ImageSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PreviewImage.Source is not null)
        {
            UpdateDisplayedImageLayout();
        }
    }

    private DipSize DisplayedImageSize()
    {
        var width = ImageSurface.ActualWidth;
        var height = ImageSurface.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("The snapshot is not ready for selection.");
        }

        return UpdateDisplayedImageLayout();
    }

    private DipSize UpdateDisplayedImageLayout()
    {
        var width = ImageSurface.ActualWidth;
        var height = ImageSurface.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("The snapshot is not ready for selection.");
        }

        var aspect = snapshot.ItemPixelSize.Width / (double)snapshot.ItemPixelSize.Height;
        var displayedWidth = Math.Min(width, height * aspect);
        var displayedHeight = displayedWidth / aspect;
        var left = (width - displayedWidth) / 2;
        var top = (height - displayedHeight) / 2;
        displayedImageLeft = left;
        displayedImageTop = top;

        PreviewImage.HorizontalAlignment = HorizontalAlignment.Left;
        PreviewImage.VerticalAlignment = VerticalAlignment.Top;
        PreviewImage.Margin = new Thickness(left, top, 0, 0);
        PreviewImage.Width = displayedWidth;
        PreviewImage.Height = displayedHeight;
        SelectionCanvas.HorizontalAlignment = HorizontalAlignment.Left;
        SelectionCanvas.VerticalAlignment = VerticalAlignment.Top;
        SelectionCanvas.Margin = new Thickness(left, top, 0, 0);
        SelectionCanvas.Width = displayedWidth;
        SelectionCanvas.Height = displayedHeight;
        RenderSelectionRectangle();
        return new DipSize(displayedWidth, displayedHeight);
    }

    private void RenderSelectionRectangle()
    {
        if (currentSelection is not DipRect selection)
        {
            SelectionRectangle.Visibility = Visibility.Collapsed;
            return;
        }

        SelectionRectangle.Margin = new Thickness(
            displayedImageLeft + selection.Left,
            displayedImageTop + selection.Top,
            0,
            0);
        SelectionRectangle.Width = selection.Width;
        SelectionRectangle.Height = selection.Height;
        SelectionRectangle.Visibility = Visibility.Visible;
    }

    private void FinishPointerDrag(Pointer pointer)
    {
        if (pointerCaptured)
        {
            SelectionCanvas.ReleasePointerCapture(pointer);
        }

        pointerCaptured = false;
        dragStart = null;
    }

    private void CancelPointerDrag(string status)
    {
        pointerCaptured = false;
        dragStart = null;
        currentSelection = null;
        SelectionRectangle.Visibility = Visibility.Collapsed;
        SelectionStatus.Text = status;
    }

    private void Complete(CaptureSelectionCoordinates? result)
    {
        if (completed)
        {
            return;
        }

        completed = true;
        completion.TrySetResult(result);
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs e)
    {
        convertedDisplayBitmap?.Dispose();
        convertedDisplayBitmap = null;

        if (!completed)
        {
            completed = true;
            completion.TrySetResult(null);
        }
    }

    private void CompletePreviewFailure(Exception exception)
    {
        if (completed)
        {
            return;
        }

        completed = true;
        completion.TrySetException(new RegionSelectorPreviewException(
            "SoftwareBitmapSource.SetBitmapAsync",
            exception));
        Close();
    }
}

public sealed class RegionSelectorPreviewException : InvalidOperationException
{
    public RegionSelectorPreviewException(string stage, Exception innerException)
        : base(
            $"Region selector preview failed at '{stage}': {innerException.GetType().Name} (HRESULT 0x{innerException.HResult:X8}).",
            innerException)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(innerException);
        if (stage.Trim().Length == 0)
        {
            throw new ArgumentException("A preview stage is required.", nameof(stage));
        }

        Stage = stage.Trim();
        ErrorType = innerException.GetType().Name;
        ErrorHResult = innerException.HResult;
    }

    public string Stage { get; }

    public string ErrorType { get; }

    public int ErrorHResult { get; }
}

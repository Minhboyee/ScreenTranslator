using Translator.Core;

namespace Translator.Windows;

public readonly record struct OcrLineOverlayPlacement(
    PhysicalPixelRect SourceBounds,
    PhysicalPixelRect OverlayBounds);

public static class OcrLineOverlayProjector
{
    public static PhysicalPixelRect Project(
        OcrText line,
        WindowCaptureSelection selection)
    {
        return ProjectToDesktop(line, selection);
    }

    public static PhysicalPixelRect ProjectToDesktop(
        OcrText line,
        WindowCaptureSelection selection)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(selection);
        return ProjectToDesktop(
            line.Bounds,
            selection.ItemLocalCrop,
            selection.DesktopScreenSelection);
    }

    public static PhysicalPixelRect ProjectToDesktop(
        PhysicalPixelRect cropLocalLineBounds,
        ItemLocalCropRect selectedCrop,
        DesktopScreenSelectionRect desktopCropBounds)
    {
        ValidateCropLocalBounds(cropLocalLineBounds, selectedCrop);

        // The OCR bitmap starts at (0, 0) for the selected crop. Make the
        // crop origin explicit before subtracting it again for the selected
        // crop's desktop mapping. This keeps the contract correct for crops
        // with a non-zero origin and avoids using the whole capture region.
        var itemLeft = checked(selectedCrop.Left + cropLocalLineBounds.Left);
        var itemTop = checked(selectedCrop.Top + cropLocalLineBounds.Top);
        var itemRight = checked(itemLeft + cropLocalLineBounds.Width);
        var itemBottom = checked(itemTop + cropLocalLineBounds.Height);

        var left = MapEdge(
            itemLeft - selectedCrop.Left,
            selectedCrop.Width,
            desktopCropBounds.Left,
            desktopCropBounds.Width,
            roundUp: false);
        var top = MapEdge(
            itemTop - selectedCrop.Top,
            selectedCrop.Height,
            desktopCropBounds.Top,
            desktopCropBounds.Height,
            roundUp: false);
        var right = MapEdge(
            itemRight - selectedCrop.Left,
            selectedCrop.Width,
            desktopCropBounds.Left,
            desktopCropBounds.Width,
            roundUp: true);
        var bottom = MapEdge(
            itemBottom - selectedCrop.Top,
            selectedCrop.Height,
            desktopCropBounds.Top,
            desktopCropBounds.Height,
            roundUp: true);

        return new PhysicalPixelRect(left, top, checked(right - left), checked(bottom - top));
    }

    public static PhysicalPixelRect ProjectFromCaptureDesktopBounds(
        OcrText line,
        WindowCaptureSelection selection,
        DesktopScreenSelectionRect desktopCaptureBounds)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(selection);
        ValidateCropLocalBounds(line.Bounds, selection.ItemLocalCrop);

        var itemLeft = checked(selection.ItemLocalCrop.Left + line.Bounds.Left);
        var itemTop = checked(selection.ItemLocalCrop.Top + line.Bounds.Top);
        var itemRight = checked(itemLeft + line.Bounds.Width);
        var itemBottom = checked(itemTop + line.Bounds.Height);
        var left = MapEdge(
            itemLeft,
            selection.ItemPixelSize.Width,
            desktopCaptureBounds.Left,
            desktopCaptureBounds.Width,
            roundUp: false);
        var top = MapEdge(
            itemTop,
            selection.ItemPixelSize.Height,
            desktopCaptureBounds.Top,
            desktopCaptureBounds.Height,
            roundUp: false);
        var right = MapEdge(
            itemRight,
            selection.ItemPixelSize.Width,
            desktopCaptureBounds.Left,
            desktopCaptureBounds.Width,
            roundUp: true);
        var bottom = MapEdge(
            itemBottom,
            selection.ItemPixelSize.Height,
            desktopCaptureBounds.Top,
            desktopCaptureBounds.Height,
            roundUp: true);

        return new PhysicalPixelRect(left, top, checked(right - left), checked(bottom - top));
    }

    public static PhysicalPixelRect ProjectToDesktopFromCapture(
        OcrText line,
        WindowCaptureSelection selection,
        DesktopScreenSelectionRect desktopCaptureBounds)
    {
        return ProjectFromCaptureDesktopBounds(line, selection, desktopCaptureBounds);
    }

    public static OcrLineOverlayPlacement ProjectAbove(
        OcrText line,
        WindowCaptureSelection selection,
        int overlayWidth,
        int overlayHeight,
        int gap = 0)
    {
        var sourceBounds = ProjectToDesktop(line, selection);
        return new OcrLineOverlayPlacement(
            sourceBounds,
            PlaceAbove(sourceBounds, overlayWidth, overlayHeight, gap));
    }

    public static PhysicalPixelRect PlaceAbove(
        PhysicalPixelRect sourceBounds,
        int overlayWidth,
        int overlayHeight,
        int gap = 0)
    {
        if (overlayWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(overlayWidth));
        }

        if (overlayHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(overlayHeight));
        }

        if (gap < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gap));
        }

        var top = checked(sourceBounds.Top - overlayHeight - gap);
        return new PhysicalPixelRect(sourceBounds.Left, top, overlayWidth, overlayHeight);
    }

    private static void ValidateCropLocalBounds(
        PhysicalPixelRect lineBounds,
        ItemLocalCropRect selectedCrop)
    {
        if (lineBounds.Left < 0 ||
            lineBounds.Top < 0 ||
            lineBounds.Right > selectedCrop.Width ||
            lineBounds.Bottom > selectedCrop.Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lineBounds),
                "OCR line bounds must be local to and contained by the selected crop.");
        }
    }

    private static int MapEdge(
        int sourceEdge,
        int sourceSize,
        int destinationOrigin,
        int destinationSize,
        bool roundUp)
    {
        var mapped = destinationOrigin + sourceEdge / (double)sourceSize * destinationSize;
        var rounded = roundUp ? Math.Ceiling(mapped) : Math.Floor(mapped);
        return checked((int)rounded);
    }
}

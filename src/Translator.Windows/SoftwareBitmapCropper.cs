using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Translator.Windows;

public static class SoftwareBitmapCropper
{
    private const int BytesPerPixel = 4;

    public static Task<SoftwareBitmap> CropAsync(
        SoftwareBitmap source,
        ItemLocalCropRect crop,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        if (source.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
        {
            throw new ArgumentException("Realtime OCR cropping requires a BGRA8 bitmap.", nameof(source));
        }

        var contentSize = new CaptureItemPixelSize(source.PixelWidth, source.PixelHeight);
        SoftwareBitmapCropContract.ToSourceBounds(crop, contentSize);

        var sourceStride = checked(source.PixelWidth * BytesPerPixel);
        var sourcePixels = ReadPixels(source, sourceStride, source.PixelHeight);
        var destinationStride = checked(crop.Width * BytesPerPixel);
        var destinationPixels = new byte[checked(destinationStride * crop.Height)];
        CopyBgra8(
            sourcePixels,
            source.PixelWidth,
            source.PixelHeight,
            sourceStride,
            crop,
            destinationPixels,
            destinationStride);

        cancellationToken.ThrowIfCancellationRequested();
        var cropped = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8,
            crop.Width,
            crop.Height,
            BitmapAlphaMode.Premultiplied);
        try
        {
            WritePixels(cropped, destinationPixels);
            return Task.FromResult(cropped);
        }
        catch
        {
            cropped.Dispose();
            throw;
        }
    }

    public static void CopyBgra8(
        ReadOnlySpan<byte> sourcePixels,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        ItemLocalCropRect crop,
        Span<byte> destinationPixels,
        int destinationStride)
    {
        if (sourceWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        }

        if (sourceHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceHeight));
        }

        if (sourceStride < checked(sourceWidth * BytesPerPixel))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceStride));
        }

        if (sourcePixels.Length < checked(sourceStride * sourceHeight))
        {
            throw new ArgumentException("The source pixel buffer is smaller than the bitmap.", nameof(sourcePixels));
        }

        CaptureCropContract.Validate(crop, new CaptureItemPixelSize(sourceWidth, sourceHeight));
        if (destinationStride < checked(crop.Width * BytesPerPixel))
        {
            throw new ArgumentOutOfRangeException(nameof(destinationStride));
        }

        if (destinationPixels.Length < checked(destinationStride * crop.Height))
        {
            throw new ArgumentException("The destination pixel buffer is smaller than the crop.", nameof(destinationPixels));
        }

        var rowBytes = checked(crop.Width * BytesPerPixel);
        for (var row = 0; row < crop.Height; row++)
        {
            sourcePixels
                .Slice(checked((crop.Top + row) * sourceStride + crop.Left * BytesPerPixel), rowBytes)
                .CopyTo(destinationPixels.Slice(row * destinationStride, rowBytes));
        }
    }

    private static byte[] ReadPixels(SoftwareBitmap bitmap, int stride, int height)
    {
        var bytes = new byte[checked(stride * height)];
        var buffer = new global::Windows.Storage.Streams.Buffer((uint)bytes.Length)
        {
            Length = (uint)bytes.Length
        };

        bitmap.CopyToBuffer(buffer);
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static void WritePixels(SoftwareBitmap bitmap, byte[] pixels)
    {
        using var writer = new DataWriter();
        writer.WriteBytes(pixels);
        var buffer = writer.DetachBuffer();
        bitmap.CopyFromBuffer(buffer);
    }
}

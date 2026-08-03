using System.Runtime.InteropServices.WindowsRuntime;
using Translator.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Translator.Windows;

public static class OcrLineAppearanceSampler
{
    private const int BytesPerPixel = 4;
    private const int MaximumGridEdge = 8;

    public static OcrResult AttachHints(OcrResult document, SoftwareBitmap ocrCrop)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(ocrCrop);

        if (document.Text.Count == 0)
        {
            return document;
        }

        var pixels = ReadBgra8Pixels(ocrCrop);
        var stride = checked(ocrCrop.PixelWidth * BytesPerPixel);
        return new OcrResult(document.Text.Select(line =>
            line.WithAppearance(new OcrLineAppearanceHint(
                SampleBgra8(
                    pixels,
                    ocrCrop.PixelWidth,
                    ocrCrop.PixelHeight,
                    stride,
                    line.Bounds)))));
    }

    public static double SampleBgra8(
        ReadOnlySpan<byte> pixels,
        int pixelWidth,
        int pixelHeight,
        int stride,
        PhysicalPixelRect cropLocalBounds)
    {
        if (pixelWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        }

        if (pixelHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelHeight));
        }

        if (stride < checked(pixelWidth * BytesPerPixel))
        {
            throw new ArgumentOutOfRangeException(nameof(stride));
        }

        if (pixels.Length < checked(stride * pixelHeight))
        {
            throw new ArgumentException("The pixel buffer is smaller than the bitmap.", nameof(pixels));
        }

        if (cropLocalBounds.Left < 0 ||
            cropLocalBounds.Top < 0 ||
            cropLocalBounds.Right > pixelWidth ||
            cropLocalBounds.Bottom > pixelHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(cropLocalBounds));
        }

        if (cropLocalBounds.Width == 0 || cropLocalBounds.Height == 0)
        {
            return 0;
        }

        var columns = Math.Min(MaximumGridEdge, cropLocalBounds.Width);
        var rows = Math.Min(MaximumGridEdge, cropLocalBounds.Height);
        var sum = 0d;
        var sampleCount = 0;

        for (var row = 0; row < rows; row++)
        {
            var y = cropLocalBounds.Top + (row * cropLocalBounds.Height + cropLocalBounds.Height / 2) / rows;
            for (var column = 0; column < columns; column++)
            {
                var x = cropLocalBounds.Left +
                        (column * cropLocalBounds.Width + cropLocalBounds.Width / 2) / columns;
                var pixelOffset = checked(y * stride + x * BytesPerPixel);
                var blue = pixels[pixelOffset];
                var green = pixels[pixelOffset + 1];
                var red = pixels[pixelOffset + 2];
                sum += RelativeLuminance(red, green, blue);
                sampleCount++;
            }
        }

        return sum / sampleCount;
    }

    public static double RelativeLuminance(byte red, byte green, byte blue)
    {
        return 0.2126 * ToLinear(red) +
               0.7152 * ToLinear(green) +
               0.0722 * ToLinear(blue);
    }

    private static byte[] ReadBgra8Pixels(SoftwareBitmap bitmap)
    {
        if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
        {
            throw new ArgumentException("OCR appearance sampling requires a BGRA8 bitmap.", nameof(bitmap));
        }

        var byteCount = checked(bitmap.PixelWidth * bitmap.PixelHeight * BytesPerPixel);
        var buffer = new global::Windows.Storage.Streams.Buffer((uint)byteCount);
        buffer.Length = (uint)byteCount;
        bitmap.CopyToBuffer(buffer);
        using var reader = DataReader.FromBuffer(buffer);
        var pixels = new byte[byteCount];
        reader.ReadBytes(pixels);
        return pixels;
    }

    private static double ToLinear(byte channel)
    {
        var srgb = channel / 255d;
        return srgb <= 0.04045
            ? srgb / 12.92
            : Math.Pow((srgb + 0.055) / 1.055, 2.4);
    }
}

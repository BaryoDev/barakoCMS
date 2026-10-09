using System.Runtime.InteropServices;
using SkiaSharp;

namespace BarakoCMS.Tests;

/// <summary>
/// Bytes an upload accepts, one per allowed type. The upload checks the start of the file against
/// its declared type, so a test that only cares about storage still has to send the right header.
/// </summary>
internal static class FileSamples
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>A PNG signature followed by <paramref name="tail"/>: enough for the upload check, and distinct per test.</summary>
    public static byte[] Png(params byte[] tail) => [.. PngSignature, .. tail];

    /// <summary>
    /// A 4x3 GIF89a of one colour, written by hand because Skia decodes GIF but does not encode it.
    /// Every LZW code is three bits: a clear code before each pair of pixels keeps the table from
    /// growing past the point where the code width would change.
    /// </summary>
    private static readonly byte[] Gif =
    [
        .. "GIF89a"u8, 0x04, 0x00, 0x03, 0x00, 0x80, 0x00, 0x00, 0xC8, 0x28, 0x28, 0x00, 0x00, 0x00,
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x03, 0x00, 0x00, 0x02,
        0x08, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, 0x40, 0x01, 0x00, 0x3B,
    ];

    /// <summary>A real, decodable image in the given format.</summary>
    public static byte[] Image(string contentType)
    {
        if (contentType == "image/gif")
        {
            return [.. Gif];
        }

        var format = contentType switch
        {
            "image/png" => SKEncodedImageFormat.Png,
            "image/jpeg" => SKEncodedImageFormat.Jpeg,
            "image/webp" => SKEncodedImageFormat.Webp,
            _ => throw new ArgumentOutOfRangeException(nameof(contentType), contentType, null),
        };

        using var bitmap = new SKBitmap(4, 3);
        bitmap.Erase(new SKColor(200, 40, 40));
        return Encode(bitmap, format);
    }

    /// <summary>
    /// A real image of the given size. Noise rather than a flat fill so the encoded bytes are not so
    /// small that a resize and an original could coincide.
    /// </summary>
    public static byte[] Noise(int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var random = new Random(width * 31 + height);

        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)random.Next(256);
            pixels[i + 1] = (byte)random.Next(256);
            pixels[i + 2] = (byte)random.Next(256);
            pixels[i + 3] = 255;
        }
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);

        return Encode(bitmap, format);
    }

    /// <summary>The width and format the header claims, read without decoding.</summary>
    public static (int Width, int Height, SKEncodedImageFormat Format) Identify(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data)
            ?? throw new InvalidOperationException("Not an image Skia can read");
        return (codec.Info.Width, codec.Info.Height, codec.EncodedFormat);
    }

    public static int WidthOf(byte[] bytes) => Identify(bytes).Width;

    private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90)
            ?? throw new InvalidOperationException($"Skia could not encode {format}");
        return data.ToArray();
    }

    /// <summary>
    /// The start of an AVIF file as libavif writes it: an <c>ftyp</c> box with major brand
    /// <c>avif</c>. Skia has no AVIF encoder, and the upload only reads the header.
    /// </summary>
    public static byte[] Avif() =>
    [
        0x00, 0x00, 0x00, 0x20, .. "ftypavif"u8, 0x00, 0x00, 0x00, 0x00, .. "avifmif1miafMA1B"u8,
    ];

    /// <summary>A minimal PDF.</summary>
    public static byte[] Pdf() =>
        System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj << /Type /Catalog >> endobj\ntrailer << /Root 1 0 R >>\n%%EOF\n");

    /// <summary>A valid sample for any allowed type.</summary>
    public static byte[] For(string contentType) => contentType switch
    {
        "image/avif" => Avif(),
        "application/pdf" => Pdf(),
        _ => Image(contentType),
    };
}

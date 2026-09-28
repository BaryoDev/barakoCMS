using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

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

    /// <summary>A real, decodable image in the given format.</summary>
    public static byte[] Image(string contentType)
    {
        using var image = new Image<Rgba32>(4, 3, new Rgba32(200, 40, 40));
        using var output = new MemoryStream();
        switch (contentType)
        {
            case "image/png": image.Save(output, new PngEncoder()); break;
            case "image/jpeg": image.Save(output, new JpegEncoder()); break;
            case "image/gif": image.Save(output, new GifEncoder()); break;
            case "image/webp": image.Save(output, new WebpEncoder()); break;
            default: throw new ArgumentOutOfRangeException(nameof(contentType), contentType, null);
        }
        return output.ToArray();
    }

    /// <summary>
    /// The start of an AVIF file as libavif writes it: an <c>ftyp</c> box with major brand
    /// <c>avif</c>. ImageSharp has no AVIF encoder, and the upload only reads the header.
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

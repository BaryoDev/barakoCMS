using SkiaSharp;

namespace BarakoCMS.Files;

/// <summary>An image's width and height in pixels, the way it is shown.</summary>
internal readonly record struct ImageSize(int Width, int Height);

/// <summary>
/// Reads an image's size from its header, so a frontend can reserve the space an <c>img</c> takes
/// before it loads.
/// </summary>
/// <remarks>
/// Only the header is read and no pixel is decoded, so a large or hostile image costs nothing more
/// than its upload already did. A JPEG tagged as turned a quarter reports its turned size, the way a
/// browser shows it, which is also the size its variants are stored at. PNG, JPEG, GIF and WebP;
/// anything else, AVIF and PDF included, has no size.
/// </remarks>
internal static class ImageDimensions
{
    private static readonly string[] Readable = ["image/png", "image/jpeg", "image/gif", "image/webp"];

    public static bool CanRead(string contentType) =>
        Readable.Contains(contentType, StringComparer.OrdinalIgnoreCase);

    public static ImageSize? Read(string contentType, Stream content)
    {
        if (!CanRead(contentType))
        {
            return null;
        }

        try
        {
            using var codec = SKCodec.Create(content);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
            {
                return null;
            }

            var turned = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
                or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
            return turned
                ? new ImageSize(codec.Info.Height, codec.Info.Width)
                : new ImageSize(codec.Info.Width, codec.Info.Height);
        }
        catch (Exception)
        {
            // A header Skia cannot read is a file with no size, not a failed upload.
            return null;
        }
    }

    public static ImageSize? Read(string contentType, byte[] content, int length) =>
        Read(contentType, new MemoryStream(content, 0, length, writable: false));
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace BarakoCMS.Files;

/// <summary>
/// Resizes with SkiaSharp.
/// </summary>
/// <remarks>
/// Skia is native, and the module carries its own Linux binary (the NoDependencies build, so no
/// fontconfig): it needs only libc and libstdc++, which every .NET runtime image already has, on
/// x64 and arm64 alike. Text is never drawn here, so the fonts that build leaves out are not missed.
///
/// Public because it is registered by type and a host assembling its own container has to be able
/// to construct one, the same reason <see cref="ClamAvScanner"/> and <see cref="PostgresFileStorage"/>
/// are.
/// </remarks>
public sealed class SkiaImageResizer : IImageResizer
{
    /// <summary>
    /// The types this will decode, which is narrower than what upload accepts on purpose.
    /// </summary>
    /// <remarks>
    /// GIF is left out because resizing an animated one resamples every frame, which is a request
    /// whose cost is set by the file rather than by the requested width, on an anonymous endpoint.
    /// AVIF is left out because the bundled Skia has no AVIF decoder. Both are served at full size.
    /// </remarks>
    private static readonly string[] Resizable = ["image/png", "image/jpeg", "image/webp"];

    /// <summary>
    /// The quality JPEG and lossy WebP variants are written at.
    /// </summary>
    /// <remarks>
    /// Fixed rather than read from the source. Estimating a JPEG's quality means parsing its
    /// quantisation tables and matching them against the IJG scale, which is guesswork for any
    /// encoder that did not use those tables, and a variant is a smaller copy for display.
    /// </remarks>
    private const int LossyQuality = 75;

    private readonly ImageVariantOptions _options;
    private readonly ILogger _logger;

    public SkiaImageResizer(IConfiguration configuration, ILogger<SkiaImageResizer> logger)
        : this(configuration, (ILogger)logger)
    {
    }

    internal SkiaImageResizer(IConfiguration configuration, ILogger logger)
    {
        _logger = logger;
        _options = Read(configuration);
    }

    internal static ImageVariantOptions Read(IConfiguration configuration)
    {
        var options = new ImageVariantOptions();

        var configured = configuration[$"{ImageVariantOptions.Section}:MaxWidth"];
        if (int.TryParse(configured, out var maxWidth))
        {
            options.MaxWidth = maxWidth;
        }

        if (long.TryParse(configuration[$"{ImageVariantOptions.Section}:MaxSourcePixels"], out var pixels)
            && pixels > 0)
        {
            options.MaxSourcePixels = pixels;
        }

        return options;
    }

    /// <summary>
    /// How many decodes may be in flight across the process.
    /// </summary>
    /// <remarks>
    /// Static, because the limit is on the machine's memory rather than on any one request, and this
    /// type is resolved per scope. Sized to the processor count: a decode is CPU bound, so more
    /// concurrent decodes than cores buys nothing and costs a bitmap each.
    /// </remarks>
    private static readonly SemaphoreSlim Decodes = new(Environment.ProcessorCount, Environment.ProcessorCount);

    public bool CanResize(string contentType) =>
        !string.IsNullOrWhiteSpace(contentType)
        && Resizable.Any(t => contentType.StartsWith(t, StringComparison.OrdinalIgnoreCase));

    public async Task<byte[]?> ResizeAsync(byte[] source, int width, CancellationToken ct = default)
    {
        try
        {
            // Only bytes that carry a PNG, JPEG or WebP signature reach Skia. A row stored before
            // uploads were checked can hold anything, and Skia would otherwise try every decoder it
            // has on it, GIF, BMP, ICO and WBMP among them.
            if (!HasResizableSignature(source))
            {
                _logger.LogWarning("Not resizing to {Width}px: the bytes are not PNG, JPEG or WebP", width);
                return null;
            }

            // The header only, and released before waiting for a decode slot, so a queue of
            // requests holds their byte arrays and nothing native.
            if (ReadHeader(source) is not { } header)
            {
                _logger.LogWarning("Could not read an image header to resize to {Width}px; serving the original", width);
                return null;
            }

            if ((long)header.Width * header.Height > _options.MaxSourcePixels)
            {
                _logger.LogWarning(
                    "Not resizing a {Width}x{Height} image: over the {Max} pixel limit",
                    header.Width, header.Height, _options.MaxSourcePixels);
                return null;
            }

            // Never upscale. A 200px logo asked for at 640 is served as the 200px logo rather than
            // as a blurrier copy of itself that also costs a row and a blob to keep.
            if (header.Width <= width)
            {
                return null;
            }

            if (Output(header.Format, source) is not { } output)
            {
                return null;
            }

            // Skia decodes only the first frame, so a resized animated WebP would come back as a
            // still. The original keeps its animation, so serve that. The pixel limit above has
            // already run on it, and serving the original decodes nothing.
            if (header.Frames > 1)
            {
                return null;
            }

            // Everything past here holds a decoded bitmap, which at the default pixel limit is a
            // couple of hundred megabytes. The rate limiter caps one address, not a set of them, and
            // the pixel limit bounds one decode rather than the number running at once, so on the
            // anonymous route N simultaneous misses on the same uncached width were N simultaneous
            // decodes. Bounded by cores instead of by connections: work queues rather than the
            // process running out of memory. Waiting here is preferable to failing, and the request
            // is already cancellable.
            await Decodes.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();

                using var data = SKData.CreateCopy(source);
                using var codec = SKCodec.Create(data);
                var info = codec?.Info ?? default;
                if (codec is null || info.Width != header.Width || info.Height != header.Height)
                {
                    return null;
                }

                var decodeInfo = new SKImageInfo(
                    info.Width,
                    info.Height,
                    SKImageInfo.PlatformColorType,
                    info.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul,
                    info.ColorSpace);

                using var decoded = new SKBitmap(decodeInfo);
                if (codec.GetPixels(decodeInfo, decoded.GetPixels()) != SKCodecResult.Success)
                {
                    _logger.LogWarning("Could not decode an image to resize to {Width}px; serving the original", width);
                    return null;
                }

                // Height follows from the width, keeping the aspect ratio.
                var height = Math.Max(1, (int)Math.Round((double)info.Height * width / info.Width));

                using var resized = Downscale(decoded, width, height);
                if (resized is null)
                {
                    return null;
                }

                using var oriented = Orient(resized, codec.EncodedOrigin);
                using var pixmap = (oriented ?? resized).PeekPixels();
                using var encoded = output switch
                {
                    Variant.Png => pixmap.Encode(SKPngEncoderOptions.Default),
                    Variant.Jpeg => pixmap.Encode(new SKJpegEncoderOptions(LossyQuality)),
                    Variant.LossyWebp => pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, LossyQuality)),
                    Variant.LosslessWebp => pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, LossyQuality)),
                    _ => null,
                };

                return encoded?.ToArray();
            }
            finally
            {
                Decodes.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Bytes that do not decode, a format claimed by the content type but not present in the
            // file, a native failure inside the decoder. All of them mean the same thing to the
            // caller: there is no variant, serve the original. A download must not 500 because a
            // resize did.
            _logger.LogWarning(ex, "Could not resize an image to {Width}px; serving the original", width);
            return null;
        }
    }

    private static bool HasResizableSignature(byte[] source)
    {
        var head = source.AsSpan(0, Math.Min(source.Length, UploadTypes.HeadLength));
        foreach (var type in Resizable)
        {
            if (UploadTypes.Matches(type, head))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct Header(int Width, int Height, SKEncodedImageFormat Format, int Frames);

    private static Header? ReadHeader(byte[] source)
    {
        using var data = SKData.CreateCopy(source);
        using var codec = SKCodec.Create(data);
        return codec is null
            ? null
            : new Header(codec.Info.Width, codec.Info.Height, codec.EncodedFormat, codec.FrameCount);
    }

    private enum Variant
    {
        Png,
        Jpeg,
        LossyWebp,
        LosslessWebp,
    }

    /// <summary>
    /// The variant is written the way the original was: a PNG stays a PNG, and a lossless WebP stays
    /// lossless rather than picking up compression artefacts its owner chose to avoid.
    /// </summary>
    private static Variant? Output(SKEncodedImageFormat decoded, byte[] source) => decoded switch
    {
        SKEncodedImageFormat.Png => Variant.Png,
        SKEncodedImageFormat.Jpeg => Variant.Jpeg,
        SKEncodedImageFormat.Webp => IsLosslessWebp(source) ? Variant.LosslessWebp : Variant.LossyWebp,
        _ => null,
    };

    /// <summary>
    /// Whether the first image chunk in a WebP file is <c>VP8L</c> (lossless) rather than <c>VP8 </c>.
    /// </summary>
    /// <remarks>
    /// Walks the RIFF chunks from the header, skipping <c>VP8X</c>, <c>ICCP</c>, <c>ANIM</c> and the
    /// rest by their declared size, and stops at the end of the buffer, so a lying size ends the walk
    /// rather than reading past it. Anything it cannot read is treated as lossy, the encoder's default.
    /// </remarks>
    internal static bool IsLosslessWebp(ReadOnlySpan<byte> file)
    {
        if (file.Length < 12 || !file[..4].SequenceEqual("RIFF"u8) || !file[8..12].SequenceEqual("WEBP"u8))
        {
            return false;
        }

        var at = 12L;
        while (at + 8 <= file.Length)
        {
            var fourCc = file.Slice((int)at, 4);
            if (fourCc.SequenceEqual("VP8L"u8))
            {
                return true;
            }

            if (fourCc.SequenceEqual("VP8 "u8))
            {
                return false;
            }

            var size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(file.Slice((int)at + 4, 4));
            at += 8 + size + (size & 1);
        }

        return false;
    }

    /// <summary>
    /// Halves with a linear filter while the image is at least twice the target, then finishes with
    /// one cubic resample.
    /// </summary>
    /// <remarks>
    /// One cubic resample from a much larger source samples a few pixels out of every block and
    /// skips the rest, which aliases a photo shrunk from 4000 to 160. Each linear halving averages
    /// every pixel, so the last step is never asked to cover more than a factor of two.
    /// </remarks>
    private static SKBitmap? Downscale(SKBitmap source, int width, int height)
    {
        var current = source;
        try
        {
            while (current.Width / 2 >= width && current.Height / 2 >= height)
            {
                var half = current.Resize(
                    current.Info.WithSize(current.Width / 2, current.Height / 2),
                    new SKSamplingOptions(SKFilterMode.Linear));
                if (half is null)
                {
                    return null;
                }

                if (!ReferenceEquals(current, source))
                {
                    current.Dispose();
                }
                current = half;
            }

            var result = current.Resize(
                current.Info.WithSize(width, height),
                new SKSamplingOptions(SKCubicResampler.CatmullRom));
            return result;
        }
        finally
        {
            if (!ReferenceEquals(current, source))
            {
                current.Dispose();
            }
        }
    }

    /// <summary>
    /// The pixels turned upright per the file's EXIF orientation, or null when they already are.
    /// </summary>
    /// <remarks>
    /// The encoder does not copy EXIF to the variant, so the orientation tag a phone camera relies on
    /// would be lost and the variant would display on its side. Applying it to the pixels instead
    /// shows the variant the way the original shows.
    /// </remarks>
    private static SKBitmap? Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft)
        {
            return null;
        }

        var w = source.Width;
        var h = source.Height;
        var swaps = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

        var turned = new SKBitmap(swaps ? source.Info.WithSize(h, w) : source.Info);
        using var canvas = new SKCanvas(turned);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Translate(w, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.Translate(w, h);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Translate(0, h);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.RotateDegrees(90);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(h, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(h, w);
                canvas.RotateDegrees(90);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, w);
                canvas.RotateDegrees(-90);
                break;
        }

        // Quarter turns and flips land every pixel centre on a pixel centre, so nothing is resampled.
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        canvas.Flush();
        return turned;
    }
}

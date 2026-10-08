using BarakoCMS.Files;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The resizer on its own, with no database or HTTP: what comes out for what goes in.
/// </summary>
public class SkiaImageResizerTests
{
    private static SkiaImageResizer Resizer() =>
        new(new ConfigurationBuilder().Build(), NullLogger<SkiaImageResizer>.Instance);

    [Theory]
    [InlineData(SKEncodedImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    [InlineData(SKEncodedImageFormat.Webp)]
    public async Task A_variant_keeps_the_format_and_aspect_ratio_of_its_original(SKEncodedImageFormat format)
    {
        var original = FileSamples.Noise(400, 300, format);

        var resized = await Resizer().ResizeAsync(original, 100, TestContext.Current.CancellationToken);

        resized.Should().NotBeNull();
        var (width, height, encoded) = FileSamples.Identify(resized!);
        width.Should().Be(100);
        height.Should().Be(75);
        encoded.Should().Be(format);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(99)]
    public async Task An_image_no_wider_than_the_request_is_not_upscaled(int width)
    {
        var original = FileSamples.Noise(width, 50);

        var resized = await Resizer().ResizeAsync(original, 100, TestContext.Current.CancellationToken);

        resized.Should().BeNull("the original is served instead of a copy at the same or a larger size");
    }

    [Theory]
    [InlineData(SKWebpEncoderCompression.Lossless, true)]
    [InlineData(SKWebpEncoderCompression.Lossy, false)]
    public async Task A_webp_variant_keeps_the_compression_of_its_original(SKWebpEncoderCompression compression, bool lossless)
    {
        var original = WebpOf(FileSamples.Noise(400, 300), compression);
        SkiaImageResizer.IsLosslessWebp(original).Should().Be(lossless, "the fixture has to be what the test says it is");

        var resized = await Resizer().ResizeAsync(original, 100, TestContext.Current.CancellationToken);

        resized.Should().NotBeNull();
        FileSamples.Identify(resized!).Should().Be((100, 75, SKEncodedImageFormat.Webp));
        SkiaImageResizer.IsLosslessWebp(resized!).Should().Be(lossless);
    }

    [Fact]
    public void A_webp_whose_chunk_sizes_run_past_the_end_reads_as_lossy()
    {
        byte[] truncated = [.. "RIFF"u8, 0xFF, 0xFF, 0xFF, 0xFF, .. "WEBP"u8, .. "VP8X"u8, 0xFF, 0xFF, 0xFF, 0x7F, 0x00];

        SkiaImageResizer.IsLosslessWebp(truncated).Should().BeFalse();
    }

    [Fact]
    public async Task A_large_reduction_still_produces_the_requested_width()
    {
        var original = FileSamples.Noise(2000, 1000);

        var resized = await Resizer().ResizeAsync(original, 160, TestContext.Current.CancellationToken);

        resized.Should().NotBeNull();
        FileSamples.Identify(resized!).Should().Be((160, 80, SKEncodedImageFormat.Png));
    }

    /// <summary>
    /// A photo stored turned or mirrored, with an EXIF orientation tag saying how. The variant carries
    /// no EXIF, so the turn has to be in its pixels or it displays wrong. The fixture is blue with a
    /// red block in its stored top-left corner, and the test asserts which corner the block ends in.
    /// </summary>
    [Theory]
    [InlineData(1, SKEncodedOrigin.TopLeft, 100, 75, Corner.TopLeft)]
    [InlineData(2, SKEncodedOrigin.TopRight, 100, 75, Corner.TopRight)]
    [InlineData(3, SKEncodedOrigin.BottomRight, 100, 75, Corner.BottomRight)]
    [InlineData(4, SKEncodedOrigin.BottomLeft, 100, 75, Corner.BottomLeft)]
    [InlineData(5, SKEncodedOrigin.LeftTop, 75, 100, Corner.TopLeft)]
    [InlineData(6, SKEncodedOrigin.RightTop, 75, 100, Corner.TopRight)]
    [InlineData(7, SKEncodedOrigin.RightBottom, 75, 100, Corner.BottomRight)]
    [InlineData(8, SKEncodedOrigin.LeftBottom, 75, 100, Corner.BottomLeft)]
    public async Task An_oriented_jpeg_is_turned_upright_in_its_variant(
        int tag, SKEncodedOrigin origin, int width, int height, Corner red)
    {
        var stored = WithOrientation(RedCornerJpeg(400, 300), (ushort)tag);
        OriginOf(stored).Should().Be(origin, "the fixture has to carry the tag for this to test anything");

        var resized = await Resizer().ResizeAsync(stored, 100, TestContext.Current.CancellationToken);

        resized.Should().NotBeNull();
        OriginOf(resized!).Should().Be(SKEncodedOrigin.TopLeft);
        using var bitmap = SKBitmap.Decode(resized);
        bitmap.Width.Should().Be(width, "the rung applies to the stored width, before turning");
        bitmap.Height.Should().Be(height);

        IsRed(Sample(bitmap, red)).Should().BeTrue($"the stored top-left corner shows at {red}");
        IsRed(Sample(bitmap, Opposite(red))).Should().BeFalse("only one corner is red");
    }

    public enum Corner
    {
        TopLeft,
        TopRight,
        BottomRight,
        BottomLeft,
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    [InlineData(SKEncodedImageFormat.Webp)]
    public async Task A_source_over_the_pixel_limit_is_refused_in_every_resizable_format(SKEncodedImageFormat format)
    {
        var image = FileSamples.Noise(400, 400, format);
        var strict = new SkiaImageResizer(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{ImageVariantOptions.Section}:MaxSourcePixels"] = "1",
                })
                .Build(),
            NullLogger<SkiaImageResizer>.Instance);

        (await strict.ResizeAsync(image, 100, TestContext.Current.CancellationToken))
            .Should().BeNull("160000 pixels is over a limit of one");
        (await Resizer().ResizeAsync(image, 100, TestContext.Current.CancellationToken))
            .Should().NotBeNull("the same image at the default limit still resizes");
    }

    /// <summary>
    /// Two frames, so Skia would hand back only the first and the animation would be lost. Paired
    /// with the same frame as a still WebP, which does resize, so a resizer that declines every WebP
    /// does not pass.
    /// </summary>
    [Fact]
    public async Task An_animated_webp_is_served_as_the_original()
    {
        var still = WebpOf(FileSamples.Noise(400, 300), SKWebpEncoderCompression.Lossless);
        var animated = AnimatedWebp(still, 400, 300, frames: 2);
        using (var data = SKData.CreateCopy(animated))
        using (var codec = SKCodec.Create(data))
        {
            codec.Should().NotBeNull("the fixture has to be a WebP Skia reads");
            codec.FrameCount.Should().Be(2);
            codec.Info.Width.Should().Be(400, "wider than the request, so only the frame count can decline it");
        }

        (await Resizer().ResizeAsync(animated, 100, TestContext.Current.CancellationToken)).Should().BeNull();
        (await Resizer().ResizeAsync(still, 100, TestContext.Current.CancellationToken)).Should().NotBeNull();
    }

    [Theory]
    [InlineData("image/gif")]
    [InlineData("image/avif")]
    [InlineData("application/pdf")]
    public void A_type_outside_png_jpeg_and_webp_is_not_resized(string contentType)
    {
        Resizer().CanResize(contentType).Should().BeFalse();
    }

    [Fact]
    public async Task Bytes_that_do_not_decode_are_declined_rather_than_thrown()
    {
        var resized = await Resizer().ResizeAsync(FileSamples.Png(1, 2, 3, 4), 100, TestContext.Current.CancellationToken);

        resized.Should().BeNull();
    }

    private static byte[] WebpOf(byte[] png, SKWebpEncoderCompression compression)
    {
        using var bitmap = SKBitmap.Decode(png);
        using var pixmap = bitmap.PeekPixels();
        using var data = pixmap.Encode(new SKWebpEncoderOptions(compression, 75))
            ?? throw new InvalidOperationException("Skia could not encode WebP");
        return data.ToArray();
    }

    private static byte[] RedCornerJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(0, 0, 255));
            using var paint = new SKPaint { Color = new SKColor(255, 0, 0) };
            canvas.DrawRect(0, 0, width / 4, width / 4, paint);
        }

        using var pixmap = bitmap.PeekPixels();
        using var data = pixmap.Encode(new SKJpegEncoderOptions(95))
            ?? throw new InvalidOperationException("Skia could not encode JPEG");
        return data.ToArray();
    }

    private static SKColor Sample(SKBitmap bitmap, Corner corner)
    {
        const int inset = 5;
        var right = bitmap.Width - 1 - inset;
        var bottom = bitmap.Height - 1 - inset;
        return corner switch
        {
            Corner.TopLeft => bitmap.GetPixel(inset, inset),
            Corner.TopRight => bitmap.GetPixel(right, inset),
            Corner.BottomRight => bitmap.GetPixel(right, bottom),
            _ => bitmap.GetPixel(inset, bottom),
        };
    }

    private static Corner Opposite(Corner corner) => (Corner)(((int)corner + 2) % 4);

    private static bool IsRed(SKColor color) => color.Red > 180 && color.Blue < 80;

    /// <summary>
    /// An extended-format WebP whose frames are all the lossless image chunk of <paramref name="still"/>.
    /// </summary>
    private static byte[] AnimatedWebp(byte[] still, int width, int height, int frames)
    {
        var image = LosslessChunk(still);

        static byte[] U24(int value) => [(byte)value, (byte)(value >> 8), (byte)(value >> 16)];
        static byte[] U32(int value) => [(byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)];
        static byte[] Chunk(ReadOnlySpan<byte> fourCc, byte[] payload) =>
            [.. fourCc, .. U32(payload.Length), .. payload, .. (payload.Length % 2 == 1 ? new byte[] { 0 } : Array.Empty<byte>())];

        var body = new List<byte>();
        body.AddRange("WEBP"u8.ToArray());
        body.AddRange(Chunk("VP8X"u8, [0x02, 0, 0, 0, .. U24(width - 1), .. U24(height - 1)]));
        body.AddRange(Chunk("ANIM"u8, [0, 0, 0, 0, 0, 0]));
        for (var i = 0; i < frames; i++)
        {
            body.AddRange(Chunk("ANMF"u8, [.. U24(0), .. U24(0), .. U24(width - 1), .. U24(height - 1), .. U24(100), 0x00, .. image]));
        }

        return [.. "RIFF"u8, .. U32(body.Count), .. body];
    }

    /// <summary>The whole <c>VP8L</c> chunk, header and padding included, wherever the encoder put it.</summary>
    private static byte[] LosslessChunk(byte[] webp)
    {
        var at = 12;
        while (at + 8 <= webp.Length)
        {
            var size = BitConverter.ToInt32(webp, at + 4);
            var end = at + 8 + size + (size & 1);
            if (webp.AsSpan(at, 4).SequenceEqual("VP8L"u8))
            {
                return webp[at..Math.Min(end, webp.Length)];
            }
            at = end;
        }

        throw new InvalidOperationException("No VP8L chunk: the still has to be lossless to borrow its frame");
    }

    private static SKEncodedOrigin OriginOf(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        return codec.EncodedOrigin;
    }

    /// <summary>Inserts an EXIF APP1 segment holding only an orientation tag after the JFIF header.</summary>
    private static byte[] WithOrientation(byte[] jpeg, ushort orientation)
    {
        byte[] exif =
        [
            .. "Exif"u8, 0x00, 0x00,
            // Big-endian TIFF header, first IFD at offset 8.
            .. "MM"u8, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,
            // One entry: tag 0x0112 (Orientation), type SHORT, count 1, value.
            0x00, 0x01,
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, (byte)(orientation >> 8), (byte)orientation, 0x00, 0x00,
            // No next IFD.
            0x00, 0x00, 0x00, 0x00,
        ];
        var length = exif.Length + 2;
        byte[] segment = [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. exif];

        // After SOI, and after APP0 when the encoder wrote one.
        var at = 2;
        if (jpeg[2] == 0xFF && jpeg[3] == 0xE0)
        {
            at += 2 + ((jpeg[4] << 8) | jpeg[5]);
        }

        return [.. jpeg[..at], .. segment, .. jpeg[at..]];
    }
}

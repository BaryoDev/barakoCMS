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
    /// A phone photo stored sideways with an EXIF orientation tag. The variant carries no EXIF, so the
    /// turn has to be in its pixels or it displays on its side.
    /// </summary>
    [Fact]
    public async Task A_sideways_jpeg_is_turned_upright_in_its_variant()
    {
        var sideways = WithOrientation(FileSamples.Noise(400, 300, SKEncodedImageFormat.Jpeg), 6);
        OriginOf(sideways).Should().Be(SKEncodedOrigin.RightTop, "the fixture has to carry the tag for this to test anything");

        var resized = await Resizer().ResizeAsync(sideways, 100, TestContext.Current.CancellationToken);

        resized.Should().NotBeNull();
        var (width, height, _) = FileSamples.Identify(resized!);
        width.Should().Be(75, "the stored 100x75 is shown turned a quarter, so its pixels are 75 wide");
        height.Should().Be(100);
        OriginOf(resized!).Should().Be(SKEncodedOrigin.TopLeft);
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

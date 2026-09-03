using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace BarakoCMS.Files;

/// <summary>
/// Resizes with SixLabors.ImageSharp.
/// </summary>
/// <remarks>
/// ImageSharp is fully managed, so it works on linux/arm64 in a container with nothing installed
/// alongside it. That is the deciding property rather than a preference: the alternatives are
/// bindings over libgdiplus or libvips, which turn "add a package" into "add a package and change
/// the Dockerfile and hope the base image has it for this architecture".
///
/// Public because it is registered by type and a host assembling its own container has to be able
/// to construct one, the same reason <see cref="ClamAvScanner"/> and <see cref="PostgresFileStorage"/>
/// are.
/// </remarks>
public sealed class ImageSharpResizer : IImageResizer
{
    /// <summary>
    /// The types this will decode, which is narrower than what upload accepts on purpose.
    /// </summary>
    /// <remarks>
    /// GIF is left out because resizing an animated one resamples every frame, which is a request
    /// whose cost is set by the file rather than by the requested width, on an anonymous endpoint.
    /// AVIF is left out because ImageSharp 3.1 has no AVIF decoder, so asking it to try produces an
    /// exception rather than an image. Both are served at full size instead.
    /// </remarks>
    private static readonly string[] Resizable = ["image/png", "image/jpeg", "image/webp"];

    private readonly ImageVariantOptions _options;
    private readonly ILogger<ImageSharpResizer> _logger;

    public ImageSharpResizer(IConfiguration configuration, ILogger<ImageSharpResizer> logger)
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
            // The header only. Identify does not decode pixels, which is the point: it is what lets
            // a pixel bomb be turned away before it costs anything to look at.
            var info = Image.Identify(source);

            if ((long)info.Width * info.Height > _options.MaxSourcePixels)
            {
                _logger.LogWarning(
                    "Not resizing a {Width}x{Height} image: over the {Max} pixel limit",
                    info.Width, info.Height, _options.MaxSourcePixels);
                return null;
            }

            // Never upscale. A 200px logo asked for at 640 is served as the 200px logo rather than
            // as a blurrier copy of itself that also costs a row and a blob to keep.
            if (info.Width <= width)
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
                using var image = await Image.LoadAsync(new MemoryStream(source), ct);

                var format = image.Metadata.DecodedImageFormat;
                if (format is null)
                {
                    return null;
                }

                // Height zero means "whatever keeps the aspect ratio".
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(width, 0),
                    Mode = ResizeMode.Max,
                }));

                using var output = new MemoryStream();
                await image.SaveAsync(output, image.Configuration.ImageFormatsManager.GetEncoder(format), ct);

                return output.ToArray();
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
            // file, a frame count ImageSharp refuses. All of them mean the same thing to the caller:
            // there is no variant, serve the original. A download must not 500 because a resize did.
            _logger.LogWarning(ex, "Could not resize an image to {Width}px; serving the original", width);
            return null;
        }
    }
}

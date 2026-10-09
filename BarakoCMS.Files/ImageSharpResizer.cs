using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BarakoCMS.Files;

/// <summary>
/// The old name of <see cref="SkiaImageResizer"/>, kept so a host that constructs it by type still
/// compiles. It no longer uses ImageSharp.
/// </summary>
[Obsolete("Use SkiaImageResizer. This name is kept only for hosts that construct it by type. Removal planned for barakoCMS 5.0.")]
public sealed class ImageSharpResizer : IImageResizer
{
    private readonly SkiaImageResizer _inner;

    public ImageSharpResizer(IConfiguration configuration, ILogger<ImageSharpResizer> logger)
    {
        _inner = new SkiaImageResizer(configuration, (ILogger)logger);
    }

    public bool CanResize(string contentType) => _inner.CanResize(contentType);

    public Task<byte[]?> ResizeAsync(byte[] source, int width, CancellationToken ct = default) =>
        _inner.ResizeAsync(source, width, ct);
}

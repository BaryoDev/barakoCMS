using System.Buffers.Binary;
using System.Security.Cryptography;
using Marten;
using Microsoft.Extensions.Configuration;

namespace BarakoCMS.Files;

/// <summary>
/// What a download should serve: a file, or a refusal to be reported as a 400.
/// </summary>
/// <param name="File">The original when no variant applies, else the derived record.</param>
/// <param name="Refused">
/// Set only when the caller asked for something outside the allowed range. Every other "no variant"
/// answer is not a refusal, it is the original, because a frontend that puts <c>?w=</c> on every
/// asset URL should not break on the one that is a PDF.
/// </param>
public readonly record struct VariantResult(StoredFile File, string? Refused);

/// <summary>
/// Resolves <c>?w=</c> to the file a download should actually send, making and caching the variant
/// the first time it is asked for.
/// </summary>
/// <remarks>
/// Shared between the authenticated and the public download because both need the identical
/// behaviour and neither may have its own version of it. What is deliberately NOT shared is the
/// access decision: this type is only ever handed a file the calling endpoint has already decided
/// the caller may have, and the variant it returns inherits that decision rather than carrying one
/// of its own. A derived record is never addressable by its own id (both endpoints refuse one), so
/// there is exactly one place where "may this caller read this?" is answered, on the original.
/// </remarks>
public sealed class ImageVariants
{
    private readonly IDocumentSession _session;
    private readonly IFileStorage _storage;
    private readonly IImageResizer _resizer;
    private readonly ImageVariantOptions _options;

    public ImageVariants(
        IDocumentSession session,
        IFileStorage storage,
        IImageResizer resizer,
        IConfiguration configuration)
    {
        _session = session;
        _storage = storage;
        _resizer = resizer;
        _options = ImageSharpResizer.Read(configuration);
    }

    public async Task<VariantResult> ResolveAsync(StoredFile original, int? requested, CancellationToken ct)
    {
        if (requested is null || !_options.Enabled)
        {
            // No width asked for, or an operator who set MaxWidth to zero. Either way this is
            // byte for byte the answer this route gave before variants existed.
            return new VariantResult(original, null);
        }

        // Asked before the width is checked, so a PDF is served unchanged whatever the width says.
        // The other order refused ?w=5000 on a PDF with a 400, which contradicted the promise that a
        // frontend can append ?w= to every asset URL: it broke on any width over the cap.
        if (!_resizer.CanResize(original.ContentType))
        {
            return new VariantResult(original, null);
        }

        var width = _options.Snap(requested.Value);
        if (width is null)
        {
            return new VariantResult(original, $"w must be between 1 and {_options.MaxWidth}.");
        }

        var id = VariantId(original.Id, width.Value);

        var cached = await _session.LoadAsync<StoredFile>(id, ct);
        if (cached is not null)
        {
            return new VariantResult(cached, null);
        }

        var source = await _storage.GetAsync(original.StorageKey, ct);
        if (source is null)
        {
            return new VariantResult(original, null);
        }

        var resized = await _resizer.ResizeAsync(source, width.Value, ct);
        if (resized is null)
        {
            return new VariantResult(original, null);
        }

        var key = VariantKey(original.StorageKey, width.Value);
        var stored = await _storage.PutAsync(
            new MemoryStream(resized), key, original.ContentType, original.IsPublic, ct);

        var variant = new StoredFile
        {
            // Derived from the parent and the width rather than random, so two requests racing for
            // the same variant upsert one row instead of storing two, and so a lookup is a load by
            // id rather than a query.
            Id = id,
            ParentFileId = original.Id,
            VariantWidth = width.Value,
            FileName = VariantName(original.FileName, width.Value),
            ContentType = original.ContentType,
            Size = resized.Length,
            Provider = _storage.Provider,
            StorageKey = stored.Key,
            // Mirrors the original so the bytes land with the same public-ness on a store that has
            // the concept. It is not what makes the variant safe; not being addressable is.
            IsPublic = original.IsPublic,
            PublicUrl = stored.PublicUrl,
            UploadedBy = original.UploadedBy,
            CreatedAt = DateTime.UtcNow,
        };

        _session.Store(variant);
        await _session.SaveChangesAsync(ct);

        return new VariantResult(variant, null);
    }

    /// <summary>The same parent and width always give the same id.</summary>
    internal static Guid VariantId(Guid parent, int width)
    {
        Span<byte> seed = stackalloc byte[20];
        parent.TryWriteBytes(seed);
        BinaryPrimitives.WriteInt32LittleEndian(seed[16..], width);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(seed, hash);

        return new Guid(hash[..16]);
    }

    /// <summary>Keeps the extension, because an object store reads the content type off the key.</summary>
    internal static string VariantKey(string parentKey, int width)
    {
        var ext = Path.GetExtension(parentKey);
        var stem = string.IsNullOrEmpty(ext) ? parentKey : parentKey[..^ext.Length];
        return $"{stem}_w{width}{ext}";
    }

    internal static string VariantName(string fileName, int width)
    {
        var ext = Path.GetExtension(fileName);
        var stem = string.IsNullOrEmpty(ext) ? fileName : fileName[..^ext.Length];
        return $"{stem}_w{width}{ext}";
    }
}

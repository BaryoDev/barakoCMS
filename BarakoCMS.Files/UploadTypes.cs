using Microsoft.Net.Http.Headers;

namespace BarakoCMS.Files;

/// <summary>
/// The types an upload may be, and the check that the bytes are that type.
/// </summary>
/// <remarks>
/// The declared type comes from the client and is stored and served as sent, so it is parsed and
/// compared whole: a prefix match let through anything that merely started with an allowed type, and
/// the type decided nothing about the bytes. Every allowed type is a binary format with a fixed
/// signature at the start of the file, so the first bytes have to carry it. SVG stays out: it is
/// XML that can carry script, and a public SVG opened directly would run it on the API origin.
/// </remarks>
internal static class UploadTypes
{
    /// <summary>How many leading bytes <see cref="Matches"/> needs to decide.</summary>
    public const int HeadLength = 64;

    /// <summary>The largest file the module stores, whichever way it arrives.</summary>
    public const long MaxBytes = 10L * 1024 * 1024;

    private static readonly Dictionary<string, Func<ReadOnlySpan<byte>, bool>> Signatures =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["image/png"] = head => head.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            ["image/jpeg"] = head => head.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
            ["image/gif"] = head => head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8),
            ["image/webp"] = head => head.Length >= 12 && head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8),
            ["image/avif"] = IsAvif,
            ["application/pdf"] = head => head.StartsWith("%PDF-"u8),
        };

    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
        ["image/avif"] = ".avif",
        ["application/pdf"] = ".pdf",
    };

    /// <summary>Every type an upload may declare.</summary>
    public static IReadOnlyCollection<string> Names => Signatures.Keys;

    /// <summary>
    /// The storage key's extension for a checked type. An object store serves by the key, so it
    /// comes from the type the bytes were checked against, never from the client's file name.
    /// </summary>
    public static string Extension(string type) => Extensions[type];

    /// <summary>
    /// Whether a stored type is exactly one an upload may be, with nothing appended. A row stored
    /// before uploads were checked can carry anything its client sent.
    /// </summary>
    public static bool IsExactly(string? stored) => stored is not null && Signatures.ContainsKey(stored);

    /// <summary>
    /// The bare media type, lower case, when <paramref name="declared"/> parses and names an allowed
    /// type exactly. Parameters such as a charset are dropped. Null otherwise.
    /// </summary>
    public static string? Allowed(string? declared)
    {
        if (string.IsNullOrWhiteSpace(declared)
            || !MediaTypeHeaderValue.TryParse(declared, out var parsed)
            || !parsed.MediaType.HasValue)
        {
            return null;
        }

        var bare = parsed.MediaType.Value!.ToLowerInvariant();
        return Signatures.ContainsKey(bare) ? bare : null;
    }

    /// <summary>Whether <paramref name="head"/>, the start of the file, carries the signature of <paramref name="type"/>.</summary>
    public static bool Matches(string type, ReadOnlySpan<byte> head) =>
        Signatures.TryGetValue(type, out var check) && check(head);

    /// <summary>
    /// An ISO-BMFF <c>ftyp</c> box whose major or a compatible brand is <c>avif</c> or <c>avis</c>.
    /// The major brand is often <c>mif1</c> with AVIF listed among the compatible ones.
    /// </summary>
    private static bool IsAvif(ReadOnlySpan<byte> head)
    {
        if (head.Length < 16 || !head[4..8].SequenceEqual("ftyp"u8))
        {
            return false;
        }

        var boxSize = (int)Math.Min((uint)(head[0] << 24 | head[1] << 16 | head[2] << 8 | head[3]), (uint)head.Length);
        for (var offset = 8; offset + 4 <= boxSize; offset += 4)
        {
            if (offset == 12)
            {
                continue; // minor version, not a brand
            }

            var brand = head.Slice(offset, 4);
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads up to <see cref="HeadLength"/> bytes from the start of <paramref name="stream"/>.</summary>
    public static async Task<byte[]> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[HeadLength];
        var read = await stream.ReadAtLeastAsync(buffer, HeadLength, throwOnEndOfStream: false, ct);
        return buffer[..read];
    }
}

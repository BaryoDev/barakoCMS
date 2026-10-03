using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using barakoCMS.Models;

namespace barakoCMS.Core.Validation;

/// <summary>
/// What an <c>inlineimage</c> field accepts: a small PNG, JPEG, GIF or WebP image carried inside the
/// entry as a base64 data URI, with optional alt text.
/// </summary>
/// <remarks>
/// The value is <c>{ "url": "data:image/png;base64,...", "alt": "..." }</c> and nothing else. It is
/// written by anyone who may write the entry and delivered, possibly anonymously, into a renderer's
/// <c>&lt;img src&gt;</c>, so a write is checked in this order and the first failure refuses it:
/// the url's length before anything is decoded, the media type against the four allowed ones, the
/// payload as plain base64, the decoded size, the bytes' signature against the declared type, and
/// the width and height read from the header. No pixel is decoded on the server.
///
/// SVG is refused. It is XML that can carry script, and none of the other four can.
///
/// The type is the opt-in. A <c>url</c> or <c>string</c> field with the <c>image</c> editor still
/// holds a URL and none of this applies to it.
///
/// Errors name the field and the limit and never the value: the payload is what a caller sent, and
/// echoing up to 87 KB of it into a 400 helps nobody.
/// </remarks>
internal static class InlineImageFields
{
    public const string TypeName = "inlineimage";

    /// <summary>The largest image an inline field holds, in decoded bytes.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>Width times height, at most. 2048 by 2048.</summary>
    public const long MaxPixels = 2048L * 2048;

    public const int MaxAltLength = 500;

    public const string UrlKey = "url";
    public const string AltKey = "alt";

    private const string DataPrefix = "data:";
    private const string Base64Marker = ";base64,";

    private static readonly (string MediaType, string Prefix)[] Allowed =
    [
        ("image/png", DataPrefix + "image/png" + Base64Marker),
        ("image/jpeg", DataPrefix + "image/jpeg" + Base64Marker),
        ("image/gif", DataPrefix + "image/gif" + Base64Marker),
        ("image/webp", DataPrefix + "image/webp" + Base64Marker),
    ];

    /// <summary>Base64 characters for <see cref="MaxBytes"/>, padding included.</summary>
    public static readonly int MaxEncodedLength = (MaxBytes + 2) / 3 * 4;

    /// <summary>The longest url accepted, checked before the payload is read at all.</summary>
    public static readonly int MaxUrlLength = Allowed.Max(a => a.Prefix.Length) + MaxEncodedLength;

    private static readonly string Limit =
        $"a PNG, JPEG, GIF or WebP image of at most {MaxBytes / 1024} KB and 2048 by 2048 pixels, "
        + "as { \"url\": \"data:image/png;base64,...\", \"alt\": \"...\" }";

    public static bool Is(string? type) => string.Equals(type, TypeName, StringComparison.OrdinalIgnoreCase);

    /// <summary>For the registry: whether a present value passes every check.</summary>
    public static bool IsValid(object value) => Problem(value) is null;

    /// <summary>Why a present value cannot be stored in this field, or null.</summary>
    public static string? ValueError(FieldDefinition field, object value) =>
        Problem(value) is { } problem
            ? $"Field '{field.DisplayName}' ({field.Name}) {problem} It takes {Limit}."
            : null;

    /// <summary>
    /// Whether the write leaves this field holding what the entry already holds, so a value stored
    /// before the field became an inline image field, or under a rule since tightened, does not
    /// block saving the rest of the entry.
    /// </summary>
    public static bool IsUnchanged(FieldDefinition field, object value, Models.Content? existing)
    {
        if (existing?.Data is not { } stored)
            return false;

        foreach (var (key, held) in stored)
        {
            if (string.Equals(key, field.Name, StringComparison.OrdinalIgnoreCase) && held is not null)
                return JsonNode.DeepEquals(JsonSerializer.SerializeToNode(held), JsonSerializer.SerializeToNode(value));
        }

        return false;
    }

    /// <summary>
    /// Removes an inline image field whose stored value is not an allowed data URI, before an entry
    /// is delivered. A null is kept.
    /// </summary>
    /// <remarks>
    /// A shape check only, linear in the url and with no decoding, so a list page costs what reading
    /// it costs. It is the backstop for a value that reached the database without the write check:
    /// text stored before the field changed type, or a document written directly.
    /// </remarks>
    public static void DropUndeliverable(Dictionary<string, object> data, ContentTypeDefinition definition)
    {
        foreach (var field in definition.Fields)
        {
            if (field is null || !Is(field.Type))
                continue;

            var keys = data.Keys.Where(k => string.Equals(k, field.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var key in keys)
            {
                if (data[key] is { } value && !IsDeliverable(value))
                    data.Remove(key);
            }
        }
    }

    /// <summary>The value's shape, an allowed media type and a base64 payload, without decoding it.</summary>
    public static bool IsDeliverable(object value) =>
        TryRead(value, out var url, out var alt)
        && (alt is null || alt.Length <= MaxAltLength)
        && url.Length <= MaxUrlLength
        && MediaTypeOf(url) is { } prefix
        && IsBase64(url.AsSpan(prefix.Length));

    private static string? Problem(object value)
    {
        if (!TryRead(value, out var url, out var alt))
            return "holds an object with a 'url' and an optional 'alt', both text, and no other member.";

        if (alt is not null && alt.Length > MaxAltLength)
            return $"has alt text longer than {MaxAltLength} characters.";

        if (url.Length > MaxUrlLength)
            return "holds an image larger than the limit.";

        if (MediaTypeOf(url) is not { } prefix)
            return "holds a url that is not a base64 data URI of an allowed image type. SVG is refused.";

        var payload = url.AsSpan(prefix.Length);
        if (!IsBase64(payload))
            return "holds a data URI whose content is not base64.";

        var bytes = new byte[payload.Length / 4 * 3];
        if (!Convert.TryFromBase64Chars(payload, bytes, out var written))
            return "holds a data URI whose content is not base64.";

        if (written > MaxBytes)
            return "holds an image larger than the limit.";

        var image = bytes.AsSpan(0, written);
        var mediaType = Allowed.First(a => a.Prefix == prefix).MediaType;

        if (!Matches(mediaType, image))
            return "holds bytes that are not the image type its data URI declares.";

        if (!TryReadSize(mediaType, image, out var width, out var height))
            return "holds an image whose width and height could not be read.";

        if (width > MaxPixels || height > MaxPixels || width * height > MaxPixels)
            return "holds an image with more pixels than the limit.";

        return null;
    }

    /// <summary>
    /// Reads the value, which arrives as a dictionary from a request body or a stored document and as
    /// a <see cref="JsonElement"/> from a caller in process. Keys are matched exactly, lower case.
    /// </summary>
    private static bool TryRead(object? value, out string url, out string? alt)
    {
        url = string.Empty;
        alt = null;
        var seenUrl = false;
        var seenAlt = false;

        switch (value)
        {
            case JsonElement { ValueKind: JsonValueKind.Object } element:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals(UrlKey) && !seenUrl && property.Value.ValueKind == JsonValueKind.String)
                    {
                        url = property.Value.GetString()!;
                        seenUrl = true;
                    }
                    else if (property.NameEquals(AltKey) && !seenAlt
                             && property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null)
                    {
                        alt = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                        seenAlt = true;
                    }
                    else
                    {
                        return false;
                    }
                }

                return seenUrl;

            case IDictionary<string, object> members:
                foreach (var (key, member) in members)
                {
                    if (key == UrlKey && member is string text)
                    {
                        url = text;
                        seenUrl = true;
                    }
                    else if (key == AltKey && member is null or string)
                    {
                        alt = member as string;
                    }
                    else
                    {
                        return false;
                    }
                }

                return seenUrl;

            default:
                return false;
        }
    }

    /// <summary>The allowed prefix the url starts with, exactly and in lower case, or null.</summary>
    private static string? MediaTypeOf(string url)
    {
        foreach (var (_, prefix) in Allowed)
        {
            if (url.StartsWith(prefix, StringComparison.Ordinal))
                return prefix;
        }

        return null;
    }

    /// <summary>
    /// Plain base64: the 64 characters, padding only at the end, a length that is a multiple of four.
    /// No whitespace, which the framework's decoder would otherwise skip.
    /// </summary>
    private static bool IsBase64(ReadOnlySpan<char> payload)
    {
        if (payload.Length == 0 || payload.Length % 4 != 0)
            return false;

        var padding = payload[^1] == '=' ? payload[^2] == '=' ? 2 : 1 : 0;

        for (var i = 0; i < payload.Length - padding; i++)
        {
            var c = payload[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c == '+' || c == '/'))
                return false;
        }

        return true;
    }

    /// <summary>The signature each allowed type starts with, as the Files module's upload checks it.</summary>
    private static bool Matches(string mediaType, ReadOnlySpan<byte> head) => mediaType switch
    {
        "image/png" => head.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
        "image/jpeg" => head.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
        "image/gif" => head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8),
        "image/webp" => head.Length >= 12 && head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8),
        _ => false,
    };

    /// <summary>Width and height from the header alone. False for a header that does not say, or says zero.</summary>
    internal static bool TryReadSize(string mediaType, ReadOnlySpan<byte> image, out long width, out long height)
    {
        width = 0;
        height = 0;

        switch (mediaType)
        {
            case "image/png":
                if (image.Length < 24 || !image[12..16].SequenceEqual("IHDR"u8))
                    return false;
                width = BinaryPrimitives.ReadUInt32BigEndian(image[16..20]);
                height = BinaryPrimitives.ReadUInt32BigEndian(image[20..24]);
                break;

            case "image/gif":
                if (image.Length < 10)
                    return false;
                width = BinaryPrimitives.ReadUInt16LittleEndian(image[6..8]);
                height = BinaryPrimitives.ReadUInt16LittleEndian(image[8..10]);
                break;

            case "image/webp":
                if (!TryReadWebPSize(image, out width, out height))
                    return false;
                break;

            case "image/jpeg":
                if (!TryReadJpegSize(image, out width, out height))
                    return false;
                break;

            default:
                return false;
        }

        return width > 0 && height > 0;
    }

    private static bool TryReadWebPSize(ReadOnlySpan<byte> image, out long width, out long height)
    {
        width = 0;
        height = 0;

        if (image.Length < 30)
            return false;

        var chunk = image[12..16];

        if (chunk.SequenceEqual("VP8 "u8))
        {
            if (image[23] != 0x9D || image[24] != 0x01 || image[25] != 0x2A)
                return false;
            width = BinaryPrimitives.ReadUInt16LittleEndian(image[26..28]) & 0x3FFF;
            height = BinaryPrimitives.ReadUInt16LittleEndian(image[28..30]) & 0x3FFF;
            return true;
        }

        if (chunk.SequenceEqual("VP8L"u8))
        {
            if (image[20] != 0x2F)
                return false;
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(image[21..25]);
            width = (bits & 0x3FFF) + 1;
            height = ((bits >> 14) & 0x3FFF) + 1;
            return true;
        }

        if (chunk.SequenceEqual("VP8X"u8))
        {
            width = 1 + (image[24] | image[25] << 8 | image[26] << 16);
            height = 1 + (image[27] | image[28] << 8 | image[29] << 16);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Walks the markers to the first frame header. Each step moves forward by at least two bytes and
    /// the image is at most <see cref="MaxBytes"/>, so the walk is bounded.
    /// </summary>
    private static bool TryReadJpegSize(ReadOnlySpan<byte> image, out long width, out long height)
    {
        width = 0;
        height = 0;
        var i = 2;

        while (i + 4 <= image.Length)
        {
            if (image[i] != 0xFF)
                return false;

            int marker = image[i + 1];

            if (marker == 0xFF)
            {
                i++;
                continue;
            }

            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                i += 2;
                continue;
            }

            if (marker is 0xD9 or 0xDA)
                return false;

            int length = BinaryPrimitives.ReadUInt16BigEndian(image.Slice(i + 2, 2));
            if (length < 2)
                return false;

            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (i + 9 > image.Length)
                    return false;
                height = BinaryPrimitives.ReadUInt16BigEndian(image.Slice(i + 5, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(image.Slice(i + 7, 2));
                return true;
            }

            i += 2 + length;
        }

        return false;
    }
}

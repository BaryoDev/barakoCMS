using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;

namespace barakoCMS.Features.Workflows.Actions;

/// <summary>How many files one workflow email may carry, and how large. Zero in any of them turns attachments off.</summary>
internal sealed record EmailAttachmentLimits(int MaxCount, long MaxFileBytes, long MaxTotalBytes)
{
    public const string Section = "Workflows:Email:Attachments";
    public const string MaxCountKey = Section + ":MaxCount";
    public const string MaxFileBytesKey = Section + ":MaxFileBytes";
    public const string MaxTotalBytesKey = Section + ":MaxTotalBytes";

    public static readonly EmailAttachmentLimits Default = new(5, 10L * 1024 * 1024, 15L * 1024 * 1024);

    /// <summary>The key whose value is not a whole number of zero or more, when there is one.</summary>
    public string? InvalidKey { get; init; }

    /// <summary>
    /// Never throws. The action is built for every workflow run, so a mistyped limit must fail the
    /// emails that carry attachments and nothing else.
    /// </summary>
    public static EmailAttachmentLimits From(IConfiguration? configuration)
    {
        if (configuration is null)
        {
            return Default;
        }

        string? invalid = null;

        long Read(string key, long fallback)
        {
            var text = configuration[key];
            if (string.IsNullOrWhiteSpace(text))
            {
                return fallback;
            }

            // An array cannot be longer than int.MaxValue, so a larger limit could never be honoured.
            if (long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                && value <= int.MaxValue)
            {
                return value;
            }

            invalid ??= key;
            return 0;
        }

        var count = (int)Read(MaxCountKey, Default.MaxCount);
        var file = Read(MaxFileBytesKey, Default.MaxFileBytes);
        var total = Read(MaxTotalBytesKey, Default.MaxTotalBytes);

        return new EmailAttachmentLimits(count, file, total) { InvalidKey = invalid };
    }
}

/// <summary>
/// Turns the Email action's <c>Attachments</c> parameter into files to send, or a reason not to.
/// </summary>
/// <remarks>
/// A workflow runs with no caller, so there is nobody whose access to a file can be checked. The
/// rule is the entry's: a file is attached only when the entry the workflow is running for names it
/// in one of its fields. The parameter is usually a template filled from the entry, so its value
/// is never trusted to choose a file on its own, and the check reads the entry rather than the
/// parameter.
/// </remarks>
internal static class EmailAttachments
{
    private const int MaxFileNameLength = 255;
    private const int MaxDataDepth = 16;

    private static readonly Regex FilesLink = new(
        @"/files/(?<id>[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})(?:[/?#]|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MediaType = new(
        @"^[A-Za-z0-9!#$&^_.+-]{1,127}/[A-Za-z0-9!#$&^_.+-]{1,127}\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal sealed record Resolution(IReadOnlyList<EmailAttachment> Files, WorkflowActionResult? Refusal);

    // Permanent: the same entry, the same files and the same limits give the same answer on a retry.
    private static Resolution Refuse(string reason) =>
        new(Array.Empty<EmailAttachment>(), WorkflowActionResult.PermanentFailure(reason));

    public static async Task<Resolution> ResolveAsync(
        string named, barakoCMS.Models.Content content, IFileStore? files, EmailAttachmentLimits limits, CancellationToken ct)
    {
        if (limits.InvalidKey is { } key)
        {
            return Refuse($"{key} is not a whole number of zero or more, so no attachment can be sent.");
        }

        if (files is null or NoFileStore)
        {
            return Refuse("No module that stores files is enabled, so nothing can be attached. Enable BarakoCMS.Files and restart.");
        }

        var items = named.Split(',', ';', '\n');
        if (items.Length > limits.MaxCount)
        {
            return Refuse(
                $"The email names {items.Length} attachments, over the limit of {limits.MaxCount} in {EmailAttachmentLimits.MaxCountKey}.");
        }

        var ids = new List<Guid>(items.Length);
        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i].Trim('[', ']', '"', ' ', '\t', '\r', '\n');
            if (item.Length == 0)
            {
                return Refuse($"Attachment {i + 1} names no file. The field it is filled from is empty.");
            }

            if (!TryReadFileId(item, out var id))
            {
                return Refuse($"Attachment {i + 1} is not a file id or a link to a stored file.");
            }

            // Before the store is asked anything, so a run cannot be used to learn whether a file
            // the entry does not name exists.
            if (!References(content, id))
            {
                return Refuse(
                    $"Attachment {i + 1} is not a file of this entry. Only a file that one of the entry's fields names can be attached.");
            }

            ids.Add(id);
        }

        var found = new List<StoredFileInfo>(ids.Count);
        long declared = 0;
        for (var i = 0; i < ids.Count; i++)
        {
            var info = await files.FindAsync(ids[i], ct);
            if (info is null)
            {
                return Refuse($"Attachment {i + 1} is not a stored file of this tenant.");
            }

            if (info.Size > limits.MaxFileBytes)
            {
                return Refuse(OverFileLimit(i, limits));
            }

            declared += info.Size;
            if (declared > limits.MaxTotalBytes)
            {
                return Refuse(OverTotalLimit(limits));
            }

            found.Add(info);
        }

        var attachments = new List<EmailAttachment>(found.Count);
        long total = 0;
        for (var i = 0; i < found.Count; i++)
        {
            await using var stream = await files.OpenReadAsync(found[i].Id, ct);
            if (stream is null)
            {
                return Refuse($"Attachment {i + 1} has no stored content.");
            }

            // The recorded size was checked above. This is the bound on what is actually held in
            // memory, for a record whose size is wrong.
            var bytes = await ReadUpToAsync(stream, limits.MaxFileBytes, ct);
            if (bytes is null)
            {
                return Refuse(OverFileLimit(i, limits));
            }

            total += bytes.Length;
            if (total > limits.MaxTotalBytes)
            {
                return Refuse(OverTotalLimit(limits));
            }

            attachments.Add(new EmailAttachment
            {
                FileName = SafeFileName(found[i].FileName),
                ContentType = SafeContentType(found[i].ContentType),
                Content = bytes,
            });
        }

        return new Resolution(attachments, null);
    }

    private static string OverFileLimit(int index, EmailAttachmentLimits limits) =>
        $"Attachment {index + 1} is over {limits.MaxFileBytes} bytes, the limit in {EmailAttachmentLimits.MaxFileBytesKey}.";

    private static string OverTotalLimit(EmailAttachmentLimits limits) =>
        $"The attachments are over {limits.MaxTotalBytes} bytes together, the limit in {EmailAttachmentLimits.MaxTotalBytesKey}.";

    /// <summary>
    /// The id on its own, or the id in a link to either download route, with or without a query.
    /// Only the hyphenated form counts, because that is the form <see cref="References"/> looks for.
    /// </summary>
    internal static bool TryReadFileId(string item, out Guid id)
    {
        if (Guid.TryParseExact(item, "D", out id))
        {
            return true;
        }

        var link = FilesLink.Match(item);
        return link.Success && Guid.TryParseExact(link.Groups["id"].Value, "D", out id);
    }

    /// <summary>
    /// Whether any text in the entry's data holds the file's id, on its own or inside a link. The
    /// same test the Files module's where-used lookup applies to an id.
    /// </summary>
    internal static bool References(barakoCMS.Models.Content content, Guid fileId)
    {
        if (content.Data is null)
        {
            return false;
        }

        var id = fileId.ToString("D");
        return content.Data.Values.Any(value => Mentions(value, id, 0));
    }

    private static bool Mentions(object? value, string id, int depth)
    {
        if (depth > MaxDataDepth)
        {
            return false;
        }

        switch (value)
        {
            case null:
                return false;
            case string text:
                return text.Contains(id, StringComparison.OrdinalIgnoreCase);
            case JsonElement { ValueKind: JsonValueKind.String } element:
                return element.GetString()!.Contains(id, StringComparison.OrdinalIgnoreCase);
            case JsonElement { ValueKind: JsonValueKind.Array } element:
                foreach (var item in element.EnumerateArray())
                {
                    if (Mentions(item, id, depth + 1))
                    {
                        return true;
                    }
                }

                return false;
            case JsonElement { ValueKind: JsonValueKind.Object } element:
                foreach (var property in element.EnumerateObject())
                {
                    if (Mentions(property.Value, id, depth + 1))
                    {
                        return true;
                    }
                }

                return false;
            case JsonElement:
                return false;
            case IEnumerable<KeyValuePair<string, object>> map:
                return map.Any(pair => Mentions(pair.Value, id, depth + 1));
            case System.Collections.IEnumerable list:
                foreach (var item in list)
                {
                    if (Mentions(item, id, depth + 1))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// The stored name with its path, control and formatting characters removed. The name came from
    /// whoever uploaded the file and ends up in a header of the message.
    /// </summary>
    internal static string SafeFileName(string? stored)
    {
        var name = stored ?? string.Empty;
        var cut = name.LastIndexOfAny(['/', '\\']);
        if (cut >= 0)
        {
            name = name[(cut + 1)..];
        }

        var safe = new StringBuilder(Math.Min(name.Length, MaxFileNameLength));
        foreach (var rune in name.EnumerateRunes())
        {
            // Format covers the direction overrides that make "gpj.exe" read as "exe.jpg".
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                continue;
            }

            if (safe.Length + rune.Utf16SequenceLength > MaxFileNameLength)
            {
                break;
            }

            safe.Append(rune.ToString());
        }

        var result = safe.ToString().Trim();
        return result.Length == 0 ? "attachment" : result;
    }

    internal static string SafeContentType(string? stored) =>
        stored is not null && MediaType.IsMatch(stored) ? stored.ToLowerInvariant() : "application/octet-stream";

    private static async Task<byte[]?> ReadUpToAsync(Stream stream, long limit, CancellationToken ct)
    {
        using var held = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (held.Length + read > limit)
            {
                return null;
            }

            held.Write(chunk, 0, read);
        }

        return held.ToArray();
    }
}

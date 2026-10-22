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
/// A workflow runs with no caller, and nothing on an entry records who chose the files its fields
/// name, so there is no user whose right to a private file could be checked. A file is attached
/// only when the entry the workflow is running for names it in one of its fields and the file is
/// public, which means anyone holding its URL can already download it. Naming a file is not enough
/// on its own, because whoever can write the field can name any file.
///
/// The one other file that may be attached, private or not, is one an earlier step of the same
/// run declared it produced, read as <c>{{actions.N.name}}</c>. The run made it and nobody chose it
/// by writing a field. The id is checked against the files the run's own record lists for step N,
/// never against the text of the parameter or the output.
/// </remarks>
internal static class EmailAttachments
{
    private const int MaxFileNameLength = 255;
    private const int MaxDataDepth = 16;

    private static readonly Regex FilesLink = new(
        @"/files/(?<id>[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})(?:[/?#]|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // The whole parameter is one placeholder for a field of the entry, the syntax the template
    // resolver reads.
    private static readonly Regex OneField = new(
        @"^\{\{\s*data\.(?<field>[A-Za-z0-9_.]+)\s*\}\}\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // One placeholder that reads an earlier step's output, the syntax ActionOutputs reads.
    private static readonly Regex RunReference = new(
        @"^\{\{\s*actions\.(?<ordinal>[0-9]{1,4})\.(?<name>[A-Za-z0-9_]{1,64})\s*\}\}\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MediaType = new(
        @"^[A-Za-z0-9!#$&^_.+-]{1,127}/[A-Za-z0-9!#$&^_.+-]{1,127}\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal sealed record Resolution(IReadOnlyList<EmailAttachment> Files, WorkflowActionResult? Refusal);

    // Permanent: the same entry, the same files and the same limits give the same answer on a retry.
    private static Resolution Refuse(string reason) =>
        new(Array.Empty<EmailAttachment>(), WorkflowActionResult.PermanentFailure(reason));

    /// <summary>One file to attach: its id, and whether the run made it.</summary>
    private readonly record struct Wanted(Guid Id, bool FromRun);

    public static async Task<Resolution> ResolveAsync(
        string named, barakoCMS.Models.Content content, IFileStore? files, EmailAttachmentLimits limits,
        WorkflowContext? run, CancellationToken ct)
    {
        if (limits.InvalidKey is { } key)
        {
            return Refuse($"{key} is not a whole number of zero or more, so no attachment can be sent.");
        }

        if (files is null or NoFileStore)
        {
            return Refuse("No module that stores files is enabled, so nothing can be attached. Enable BarakoCMS.Files and restart.");
        }

        var pieces = SplitRunReferences(named);
        var listed = pieces.Select(p => p.Text is null ? (IReadOnlyList<string>)[] : Items(p.Text, content)).ToList();
        var total = pieces.Count(p => p.Text is null) + listed.Sum(items => items.Count);
        if (total == 0)
        {
            return Refuse("The 'Attachments' parameter names no file. The list it is filled from is empty.");
        }

        if (total > limits.MaxCount)
        {
            return Refuse(
                $"The email names {total} attachments, over the limit of {limits.MaxCount} in {EmailAttachmentLimits.MaxCountKey}.");
        }

        // In the order the author wrote them. A run file named twice is one file.
        var wanted = new List<Wanted>(total);
        for (var i = 0; i < pieces.Count; i++)
        {
            if (pieces[i].Text is null)
            {
                var (file, refusal) = ReadRunFile(pieces[i].Ordinal, pieces[i].Name!, wanted.Count, run);
                if (refusal is not null)
                {
                    return new Resolution(Array.Empty<EmailAttachment>(), refusal);
                }

                var at = wanted.FindIndex(w => w.Id == file.Id);
                if (at < 0)
                {
                    wanted.Add(file);
                }
                else if (file.FromRun)
                {
                    wanted[at] = file;
                }

                continue;
            }

            if (ReadEntryFiles(listed[i], content, wanted) is { } reason)
            {
                return Refuse(reason);
            }
        }

        var (found, refused) = await FindAllAsync(wanted, files, limits, ct);
        return refused is not null
            ? new Resolution(Array.Empty<EmailAttachment>(), refused)
            : await ReadAllAsync(found, wanted, files, limits, ct);
    }

    /// <summary>Adds the files the entry names to <paramref name="wanted"/>, or says why one cannot be attached.</summary>
    private static string? ReadEntryFiles(IReadOnlyList<string> items, barakoCMS.Models.Content content, List<Wanted> wanted)
    {
        foreach (var raw in items)
        {
            var item = raw.Trim(' ', '\t', '\r', '\n');
            if (item.Length == 0)
            {
                return $"Attachment {wanted.Count + 1} names no file. The field it is filled from is empty.";
            }

            if (!TryReadFileId(item, out var id))
            {
                return $"Attachment {wanted.Count + 1} is not a file id or a link to a stored file.";
            }

            // Before the store is asked anything, so a run cannot be used to learn whether a file
            // the entry does not name exists.
            if (!References(content, id))
            {
                return NotAttachable(wanted.Count);
            }

            if (!wanted.Exists(w => w.Id == id))
            {
                wanted.Add(new Wanted(id, FromRun: false));
            }
        }

        return null;
    }

    /// <summary>One stretch of the parameter: a step output reference, or text for the entry's own rules.</summary>
    private readonly record struct Piece(int Ordinal, string? Name, string? Text);

    /// <summary>
    /// The parameter in the order it was written: each placeholder that reads an earlier step's
    /// output on its own, and the text between them for the entry's own rules. A parameter with
    /// none comes back whole.
    /// </summary>
    private static List<Piece> SplitRunReferences(string named)
    {
        var pieces = new List<Piece>();
        var text = new List<string>();
        var any = false;
        foreach (var part in named.Split(',', ';', '\n'))
        {
            var match = RunReference.Match(part.Trim());
            if (match.Success)
            {
                any = true;
                Flush();
                pieces.Add(new Piece(int.Parse(match.Groups["ordinal"].Value, CultureInfo.InvariantCulture), match.Groups["name"].Value, null));
            }
            else if (part.Trim().Length > 0)
            {
                text.Add(part);
            }
        }

        if (!any)
        {
            return [new Piece(0, null, named)];
        }

        Flush();
        return pieces;

        void Flush()
        {
            if (text.Count > 0) pieces.Add(new Piece(0, null, string.Join(',', text)));
            text.Clear();
        }
    }

    /// <summary>
    /// The file an earlier step's output names, when the run's own record says that step produced
    /// it. The output's text alone decides nothing: a step can capture any id, and only a file the
    /// action declared on its attempt is one this run made.
    /// </summary>
    private static (Wanted File, WorkflowActionResult? Refusal) ReadRunFile(int ordinal, string name, int index, WorkflowContext? run)
    {
        if (run is null)
        {
            return (default, WorkflowActionResult.PermanentFailure(
                $"Attachment {index + 1} reads a step output, which this way of running the email cannot supply."));
        }

        if (run.Ordinal >= 0 && ordinal >= (run.Undoing ? run.Ordinal + 1 : run.Ordinal))
        {
            return (default, WorkflowActionResult.PermanentFailure(
                $"Attachment {index + 1} reads action {ordinal}, which is not an earlier step than this one. "
                + "Only the output of an earlier step can be attached."));
        }

        if (!run.Outputs.TryGetValue(ordinal, out var kept) || !kept.TryGetValue(name, out var text))
        {
            // Retryable, as every other parameter that reads an output is: an earlier step can still be waiting on its own retry.
            return (default, WorkflowActionResult.Failure(
                $"Attachment {index + 1} reads {{{{actions.{ordinal}.{name}}}}}, and action {ordinal} has not "
                + "succeeded with that output captured. It is tried again once it has."));
        }

        if (!Guid.TryParseExact(text.Trim(), "D", out var id))
        {
            return (default, WorkflowActionResult.PermanentFailure($"Attachment {index + 1} is not a file id."));
        }

        return run.ProducedFiles.TryGetValue(ordinal, out var produced) && produced.Contains(id)
            ? (new Wanted(id, FromRun: true), null)
            : (default, WorkflowActionResult.PermanentFailure(NotAttachable(index)));
    }

    private static async Task<(List<StoredFileInfo> Files, WorkflowActionResult? Refusal)> FindAllAsync(
        List<Wanted> wanted, IFileStore files, EmailAttachmentLimits limits, CancellationToken ct)
    {
        var found = new List<StoredFileInfo>(wanted.Count);
        long declared = 0;
        for (var i = 0; i < wanted.Count; i++)
        {
            var info = wanted[i].FromRun
                ? await files.FindForRunAsync(wanted[i].Id, ct)
                : await files.FindPublicAsync(wanted[i].Id, ct);
            if (info is null)
            {
                return (found, WorkflowActionResult.PermanentFailure(wanted[i].FromRun ? RunFileGone(i) : NotAttachable(i)));
            }

            if (info.Size > limits.MaxFileBytes)
            {
                return (found, WorkflowActionResult.PermanentFailure(OverFileLimit(i, limits)));
            }

            declared += info.Size;
            if (declared > limits.MaxTotalBytes)
            {
                return (found, WorkflowActionResult.PermanentFailure(OverTotalLimit(limits)));
            }

            found.Add(info);
        }

        return (found, null);
    }

    private static async Task<Resolution> ReadAllAsync(
        List<StoredFileInfo> found, List<Wanted> wanted, IFileStore files, EmailAttachmentLimits limits, CancellationToken ct)
    {
        var attachments = new List<EmailAttachment>(found.Count);
        long total = 0;
        for (var i = 0; i < found.Count; i++)
        {
            await using var stream = wanted[i].FromRun
                ? await files.OpenForRunAsync(found[i].Id, ct)
                : await files.OpenPublicAsync(found[i].Id, ct);
            if (stream is null)
            {
                return Refuse(wanted[i].FromRun ? RunFileGone(i) : NotAttachable(i));
            }

            // The recorded size was checked above, before the store was asked for any bytes. This is
            // the same limit on what the stream holds, for a record whose size is wrong. It bounds
            // what is sent, not what the store read to produce the stream.
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

    private static string RunFileGone(int index) =>
        $"Attachment {index + 1} is a file an earlier step produced, and the store no longer has it.";

    // One reason for a file the entry does not name, a file this tenant does not have and a file
    // that is not public, so the run does not say which files exist.
    private static string NotAttachable(int index) =>
        $"Attachment {index + 1} cannot be attached. It has to be a public file that one of the entry's fields names.";

    /// <summary>
    /// The files the parameter names, one item each, before any of them is checked.
    /// </summary>
    /// <remarks>
    /// The runner hands this parameter over as written. When it is one placeholder for a field, the
    /// field's stored value is read from the entry: a list gives one item per element, where
    /// rendering it to text would give the list's type name. Anything else is rendered the way
    /// every other parameter is and split on commas, semicolons and line breaks.
    /// </remarks>
    internal static IReadOnlyList<string> Items(string named, barakoCMS.Models.Content content)
    {
        var field = OneField.Match(named.Trim());
        if (field.Success
            && content.Data is not null
            && content.Data.TryGetValue(field.Groups["field"].Value, out var value)
            && value is not string)
        {
            return value switch
            {
                null => new[] { string.Empty },
                JsonElement { ValueKind: JsonValueKind.Array } array => array.EnumerateArray().Select(item => Text(item)).ToArray(),
                JsonElement element => new[] { Text(element) },
                System.Collections.IEnumerable list and not System.Collections.IDictionary => list.Cast<object?>().Select(item => Text(item)).ToArray(),
                _ => new[] { Text(value) },
            };
        }

        return TemplateVariableExtractor.Resolve(named, content, TemplateValueEncoding.None).Split(',', ';', '\n');
    }

    // Text is the only thing that can name a file. Anything else becomes text that is not an id.
    private static string Text(object? value) => value switch
    {
        null => string.Empty,
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
        JsonElement { ValueKind: JsonValueKind.Null } => string.Empty,
        _ => "(not text)",
    };

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

    /// <summary>The stream's bytes, or null when there are more than <paramref name="limit"/>.</summary>
    private static async Task<byte[]?> ReadUpToAsync(Stream stream, long limit, CancellationToken ct)
    {
        // A store that already holds the file as one array hands that array over, so the bytes
        // are not copied a second time.
        if (stream is MemoryStream memory
            && memory.Position == 0
            && memory.TryGetBuffer(out var whole)
            && whole.Array is not null
            && whole.Offset == 0
            && whole.Count == whole.Array.Length)
        {
            return whole.Count > limit ? null : whole.Array;
        }

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

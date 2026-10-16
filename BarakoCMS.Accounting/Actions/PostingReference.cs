using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Marten;

namespace BarakoCMS.Accounting;

/// <summary>What a look for a workflow posting found.</summary>
/// <param name="Entry">The entry a workflow run made for the reference, or null.</param>
/// <param name="Conflict">Why the entry's id is taken by something a workflow did not make, or null.</param>
internal sealed record PostingLookup(barakoCMS.Models.Content? Entry, string? Conflict);

/// <summary>
/// How a workflow posting is found again. The reference is the subject entry's id and the action's
/// key, and the entry's own id comes from the same text, so a retry reaches the entry it already made.
/// </summary>
/// <remarks>
/// An entry counts as the posting only when a workflow run made it (<c>SourceRunId</c> is set, and a
/// client cannot set it) and it carries the reference. Anything else on that id, or any entry that
/// merely carries the reference, is not trusted: the id is the key, and a user must not be able to
/// stop a posting by making an entry first.
/// </remarks>
internal static partial class PostingReference
{
    public static string For(Guid subjectId, string actionKey) => $"{subjectId:N}:{actionKey}";

    /// <summary>The id the entry for <paramref name="reference"/> always has.</summary>
    public static Guid EntryId(string reference) => new(SHA256.HashData(Encoding.UTF8.GetBytes(reference))[..16]);

    /// <summary>Whether a reference has the form workflow postings use, which a manual entry may not take.</summary>
    public static bool IsReserved(string? reference) => reference is not null && ReservedForm().IsMatch(reference.Trim());

    [GeneratedRegex("^[0-9a-f]{32}:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedForm();

    /// <summary>The entry a workflow made for exactly this reference, none, or the conflict that blocks the posting.</summary>
    public static async Task<PostingLookup> FindAsync(IQuerySession session, string reference, CancellationToken ct)
    {
        var id = EntryId(reference);
        if (await session.LoadAsync<barakoCMS.Models.Content>(id, ct) is not { } found)
            return new PostingLookup(null, null);

        var sameReference = found.ContentType == AccountingContentTypes.JournalEntry
            && string.Equals(ContentData.AsString(ContentData.Get(found.Data, "Reference")), reference, StringComparison.Ordinal);
        return found.SourceRunId is not null && sameReference
            ? new PostingLookup(found, null)
            : new PostingLookup(null, $"Entry {id} already exists for '{reference}' and no workflow run made it.");
    }
}

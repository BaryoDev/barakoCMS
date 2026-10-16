using System.Security.Cryptography;
using System.Text;
using Marten;
using Marten.Linq.MatchesSql;

namespace BarakoCMS.Accounting;

/// <summary>
/// How a workflow posting is found again. The reference is the subject entry's id and the action's
/// key, and the entry's own id comes from the same text, so a retry reaches the entry it already made.
/// </summary>
internal static class PostingReference
{
    public static string For(Guid subjectId, string actionKey) => $"{subjectId:N}:{actionKey}";

    /// <summary>The id the entry for <paramref name="reference"/> always has.</summary>
    public static Guid EntryId(string reference) => new(SHA256.HashData(Encoding.UTF8.GetBytes(reference))[..16]);

    /// <summary>The journal entry that carries exactly this reference, or null.</summary>
    public static async Task<barakoCMS.Models.Content?> FindAsync(IQuerySession session, string reference, CancellationToken ct) =>
        await session.Query<barakoCMS.Models.Content>()
            .Where(c => c.ContentType == AccountingContentTypes.JournalEntry)
            .Where(c => c.MatchesSql("d.data -> 'Data' ->> 'Reference' = ?", reference))
            .FirstOrDefaultAsync(ct);
}

using Marten;

namespace BarakoCMS.Tagging;

/// <summary>
/// Transaction scoped advisory locks, so a check and the write that depends on it cannot be split
/// by another request. Each lock is held until the session's next commit or rollback.
/// </summary>
internal static class TaggingLocks
{
    public static async Task ForVocabularyAsync(IDocumentSession session, Guid vocabularyId, CancellationToken ct)
    {
        await session.BeginTransactionAsync(ct);
        await session.QueryAsync<int>(
            "select 1 from pg_advisory_xact_lock(hashtextextended(?, 0))", ct,
            $"barakocms:tagging:vocabulary:{session.TenantId}:{vocabularyId:D}");
    }

    public static async Task ForRecordAsync(IDocumentSession session, string contentType, string recordId, CancellationToken ct)
    {
        await session.BeginTransactionAsync(ct);
        await session.QueryAsync<int>(
            "select 1 from pg_advisory_xact_lock(hashtextextended(?, 0))", ct,
            $"barakocms:tagging:record:{session.TenantId}:{contentType}:{recordId}");
    }
}

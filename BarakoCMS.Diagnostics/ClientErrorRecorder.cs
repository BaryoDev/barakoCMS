using BarakoCMS.Diagnostics.Features.Report;
using Marten;

namespace BarakoCMS.Diagnostics;

/// <summary>
/// Records one captured error into a Marten session, deduplicating by fingerprint: a recurrence of
/// the same fault bumps <see cref="ClientError.Count"/> and reopens it rather than inserting a new
/// row. The caller owns the transaction (call SaveChangesAsync). Extracted from the endpoint so the
/// dedup/reopen behaviour can be integration-tested directly.
/// </summary>
public static class ClientErrorRecorder
{
    public const int MaxItems = 20;
    public const int MaxMessage = 2000;
    public const int MaxStack = 8000;
    public const int MaxField = 1000;

    public static async Task RecordAsync(
        IDocumentSession session,
        ReportItem item,
        string? userAgent,
        Guid? userId,
        string? username,
        CancellationToken ct = default)
    {
        var message = ClientErrorText.Trim(item.Message, MaxMessage);
        if (string.IsNullOrWhiteSpace(message)) return;

        var kind = ClientErrorText.Trim(item.Kind, 40) ?? "error";
        var source = ClientErrorText.Trim(item.Source, MaxField);
        var fingerprint = ClientErrorText.Fingerprint(kind, message, source, item.Status);

        var existing = await session.Query<ClientError>()
            .Where(e => e.Fingerprint == fingerprint)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            existing.Count += 1;
            existing.LastSeenAt = DateTime.UtcNow;
            // A recurrence after resolve reopens it — it isn't actually fixed.
            existing.Resolved = false;
            existing.ResolvedAt = null;
            if (userId is not null) { existing.UserId = userId; existing.Username = username; }
            session.Store(existing);
            return;
        }

        session.Store(new ClientError
        {
            Fingerprint = fingerprint,
            Kind = kind,
            Severity = ClientErrorText.NormalizeSeverity(item.Severity),
            Message = message,
            Stack = ClientErrorText.Trim(item.Stack, MaxStack),
            Source = source,
            Status = item.Status,
            Url = ClientErrorText.Trim(item.Url, MaxField),
            UserAgent = ClientErrorText.Trim(userAgent, MaxField),
            AppVersion = ClientErrorText.Trim(item.AppVersion, 100),
            Tenant = ClientErrorText.Trim(item.Tenant, 100),
            UserId = userId,
            Username = username,
        });
    }
}

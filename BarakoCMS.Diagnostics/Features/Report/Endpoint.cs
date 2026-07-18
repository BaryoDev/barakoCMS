using System.Security.Cryptography;
using System.Text;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Diagnostics.Features.Report;

/// <summary>One captured error from the browser. All fields are best-effort.</summary>
public class ReportItem
{
    public string? Kind { get; set; }
    public string? Severity { get; set; }
    public string? Message { get; set; }
    public string? Stack { get; set; }
    public string? Source { get; set; }
    public int? Status { get; set; }
    public string? Url { get; set; }
    public string? AppVersion { get; set; }
    public string? Tenant { get; set; }
}

public class ReportRequest
{
    public List<ReportItem> Items { get; set; } = new();
}

/// <summary>
/// POST /api/client-errors — ingest a small batch of browser errors. Anonymous, because faults
/// happen before sign-in too; strings are capped and the batch size is limited so it can't be used
/// to flood storage. Repeated faults are deduplicated by fingerprint (Count++/LastSeenAt) rather
/// than stored again.
/// </summary>
public class Endpoint : Endpoint<ReportRequest>
{
    private const int MaxItems = 20;
    private const int MaxMessage = 2000;
    private const int MaxStack = 8000;
    private const int MaxField = 1000;

    private readonly IDocumentSession _session;
    public Endpoint(IDocumentSession session) => _session = session;

    public override void Configure()
    {
        Post("/api/client-errors");
        AllowAnonymous();
    }

    public override async Task HandleAsync(ReportRequest req, CancellationToken ct)
    {
        // Best-effort identity: present only when the request carried a valid token.
        Guid? userId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var uid) ? uid : null;
        var username = User.FindFirst("Username")?.Value ?? User.Identity?.Name;

        foreach (var item in req.Items.Take(MaxItems))
        {
            var message = Trim(item.Message, MaxMessage);
            if (string.IsNullOrWhiteSpace(message)) continue;

            var kind = Trim(item.Kind, 40) ?? "error";
            var source = Trim(item.Source, MaxField);
            var fingerprint = Fingerprint(kind, message, source, item.Status);

            var existing = await _session.Query<ClientError>()
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
                _session.Store(existing);
                continue;
            }

            _session.Store(new ClientError
            {
                Fingerprint = fingerprint,
                Kind = kind,
                Severity = item.Severity == "warning" ? "warning" : "error",
                Message = message,
                Stack = Trim(item.Stack, MaxStack),
                Source = source,
                Status = item.Status,
                Url = Trim(item.Url, MaxField),
                UserAgent = Trim(HttpContext.Request.Headers.UserAgent.ToString(), MaxField),
                AppVersion = Trim(item.AppVersion, 100),
                Tenant = Trim(item.Tenant, 100),
                UserId = userId,
                Username = username,
            });
        }

        await _session.SaveChangesAsync(ct);
        await SendOkAsync(ct);
    }

    private static string? Trim(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        s = s.Trim();
        return s.Length <= max ? s : s[..max];
    }

    private static string Fingerprint(string kind, string message, string? source, int? status)
    {
        var raw = $"{kind}\n{message}\n{source}\n{status}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }
}

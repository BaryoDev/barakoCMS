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
/// happen before sign-in too; the batch size is limited and strings are capped (see
/// <see cref="ClientErrorRecorder"/>) so it can't be used to flood storage. Repeated faults are
/// deduplicated by fingerprint rather than stored again.
/// </summary>
public class Endpoint : Endpoint<ReportRequest>
{
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
        var userAgent = HttpContext.Request.Headers.UserAgent.ToString();

        foreach (var item in req.Items.Take(ClientErrorRecorder.MaxItems))
        {
            await ClientErrorRecorder.RecordAsync(_session, item, userAgent, userId, username, ct);
        }

        await _session.SaveChangesAsync(ct);
        await SendOkAsync(ct);
    }
}

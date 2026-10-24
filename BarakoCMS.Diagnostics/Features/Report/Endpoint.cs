using barakoCMS.Infrastructure.Multitenancy;
using BarakoCMS.Diagnostics.Forwarding;
using FastEndpoints;
using Marten;
// RequireRateLimiting lives here; this project is not a Web SDK project, so it is not implicitly used.
using Microsoft.AspNetCore.Builder;

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
public class Endpoint(IDocumentSession session, TenantContext tenant) : Endpoint<ReportRequest>
{
    public override void Configure()
    {
        Post("/api/client-errors");
        AllowAnonymous();
        // Unauthenticated ingest that fans out to one lookup per batch item, so it gets a tighter
        // budget than the global per-IP limit.
        Options(x => x.RequireRateLimiting("telemetry"));
    }

    public override async Task HandleAsync(ReportRequest req, CancellationToken ct)
    {
        // Best-effort identity: present only when the request carried a valid token.
        Guid? userId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var uid) ? uid : null;
        var username = User.FindFirst("Username")?.Value ?? User.Identity?.Name;
        var userAgent = HttpContext.Request.Headers.UserAgent.ToString();

        var items = req.Items.Take(ClientErrorRecorder.MaxItems).ToList();
        foreach (var item in items)
        {
            // The body is anonymous input, so the tenant it names is not believed. The one the request
            // resolved to is the only one an error is stored under.
            item.Tenant = tenant.Slug;
            await ClientErrorRecorder.RecordAsync(session, item, userAgent, userId, username, ct);
        }

        await session.SaveChangesAsync(ct);

        // After the save, and never awaited: the receiver cannot slow or fail this response.
        if (TryResolve<ClientErrorForwarder>() is { Enabled: true } forwarder)
        {
            foreach (var item in items)
            {
                if (ClientErrorRecorder.ForwardableFrom(item) is { } forwarded)
                {
                    forwarder.TryEnqueue(forwarded);
                }
            }
        }

        await Send.OkAsync(ct);
    }
}

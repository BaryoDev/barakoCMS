using FastEndpoints;
using Marten;

namespace BarakoCMS.Email.Resend;

/// <summary>
/// GET /api/email-events — the delivery problems Resend has reported (bounces, complaints, delays),
/// newest first, for the admin. Global (not tenant-scoped), like the <see cref="EmailEvent"/> store.
/// </summary>
public sealed class EmailEventsEndpoint : Endpoint<EmailEventsEndpoint.Request, IReadOnlyList<EmailEvent>>
{
    private readonly IQuerySession _session;

    public EmailEventsEndpoint(IQuerySession session) => _session = session;

    public sealed class Request
    {
        /// <summary>Cap on rows returned (1–500, default 200).</summary>
        public int Limit { get; set; } = 200;

        /// <summary>Optional filter by event type: bounced | complained | delivery_delayed.</summary>
        public string? Type { get; set; }
    }

    public override void Configure()
    {
        Get("/api/email-events");
        Roles("Admin", "SuperAdmin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var limit = Math.Clamp(req.Limit, 1, 500);
        var q = _session.Query<EmailEvent>().AsQueryable();
        if (!string.IsNullOrWhiteSpace(req.Type))
            q = q.Where(e => e.Type == req.Type);

        var events = await q.OrderByDescending(e => e.At).Take(limit).ToListAsync(ct);
        await SendOkAsync(events.ToList(), ct);
    }
}

using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using FastEndpoints;
using Marten;
using Microsoft.Extensions.Configuration;

namespace BarakoCMS.Email.Resend;

/// <summary>
/// GET /api/email-events — the delivery problems Resend has reported (bounces, complaints, delays),
/// newest first, for the admin.
/// </summary>
/// <remarks>
/// The <see cref="EmailEvent"/> store is global. Holding the capability through a global role sees
/// every event; anyone else sees the events for email the current tenant sent, which the event
/// carries from the <see cref="SentEmail"/> recorded at send time. An event with no tenant is seen
/// through a global role only.
/// </remarks>
public sealed class EmailEventsEndpoint(
    IQuerySession session,
    TenantContext tenant,
    IConfiguration configuration) : Endpoint<EmailEventsEndpoint.Request, IReadOnlyList<EmailEvent>>
{
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
        Definition.RequireCapability(
            ResendEmailCapabilities.ViewEmailEvents, ResendEmailCapabilities.LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var limit = Math.Clamp(req.Limit, 1, 500);
        var q = session.Query<EmailEvent>().AsQueryable();
        if (!string.IsNullOrWhiteSpace(req.Type))
            q = q.Where(e => e.Type == req.Type);

        if (!await PlatformScope.HoldsGloballyAsync(session, User, configuration,
                ResendEmailCapabilities.ViewEmailEvents, ResendEmailCapabilities.LegacyRoles, ct))
        {
            var slug = tenant.Slug;
            q = q.Where(e => e.Tenant == slug);
        }

        var events = await q.OrderByDescending(e => e.At).Take(limit).ToListAsync(ct);
        await Send.OkAsync(events.ToList(), ct);
    }
}

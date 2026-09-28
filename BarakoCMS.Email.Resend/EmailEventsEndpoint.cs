using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Email.Resend;

/// <summary>
/// GET /api/email-events — the delivery problems Resend has reported (bounces, complaints, delays),
/// newest first, for the admin.
/// </summary>
/// <remarks>
/// The <see cref="EmailEvent"/> store is global and an event carries no tenant, only a recipient.
/// A SuperAdmin sees every event. Anyone else sees the events for addresses that belong to active
/// members of the current tenant, which is the part of the mail a tenant's administrator sends to.
/// </remarks>
public sealed class EmailEventsEndpoint(
    IQuerySession session,
    TenantContext tenant) : Endpoint<EmailEventsEndpoint.Request, IReadOnlyList<EmailEvent>>
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

        if (!await PlatformScope.IsSuperAdminAsync(session, User, ct))
        {
            var addresses = await MemberAddressesAsync(tenant.Slug, ct);
            q = q.Where(e => e.Email.IsOneOf(addresses));
        }

        var events = await q.OrderByDescending(e => e.At).Take(limit).ToListAsync(ct);
        await Send.OkAsync(events.ToList(), ct);
    }

    /// <summary>
    /// Stored lowercased, like <see cref="EmailEvent.Email"/>, so the match does not depend on how a
    /// member typed their address.
    /// </summary>
    private async Task<string[]> MemberAddressesAsync(string slug, CancellationToken ct)
    {
        var memberIds = await session.Query<Membership>()
            .Where(m => m.TenantSlug == slug && m.Status == MembershipStatus.Active)
            .Select(m => m.UserId)
            .ToListAsync(ct);
        if (memberIds.Count == 0)
        {
            return [];
        }

        var emails = await session.Query<User>()
            .Where(u => u.Id.IsOneOf(memberIds.ToArray()))
            .Select(u => u.Email)
            .ToListAsync(ct);
        return emails.Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();
    }
}

using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Me;

internal sealed record MyRole(Guid Id, string Name);

/// <param name="Tenant">The tenant the roles and capabilities below were read in.</param>
/// <param name="Roles">The stored roles the caller holds there: global roles and the active membership's.</param>
/// <param name="Capabilities">
/// Every capability those roles carry, sorted and without repeats. A role holding <c>*</c> is
/// reported with <c>*</c> as stored, not expanded, since <c>*</c> also covers capabilities added
/// after this build.
/// </param>
internal sealed record MeResponse(
    Guid UserId,
    string Username,
    string Tenant,
    IReadOnlyList<MyRole> Roles,
    IReadOnlyList<string> Capabilities);

/// <summary>
/// GET /api/me, the signed-in caller's own roles and capabilities in the current tenant, read from
/// the store the way the API decides access, so a console can decide sensitivity and navigation the
/// same way. The seeded SuperAdmin role is <see cref="SystemRoles.SuperAdminRoleId"/> in the list.
/// </summary>
/// <remarks>
/// <para>
/// No request: nothing in the query or the route can name another user. The answer is always the
/// <c>UserId</c> the token carries.
/// </para>
/// <para>
/// The tenant is the token's own <c>tenant</c> claim when it has one, else the resolved tenant.
/// Routes under <c>/api/me</c> are exempt from the check that a token's tenant matches the
/// resolved one, so an <c>X-Tenant</c> header here could otherwise ask about a tenant the session
/// does not act in. Every other route refuses that session there anyway.
/// </para>
/// <para>
/// An API key gets a 403 from <see cref="barakoCMS.Infrastructure.Auth.ApiKeyScopeProcessor"/>, as
/// on every route outside the content API. The response is sent <c>Cache-Control: no-store</c>
/// through the <c>/api/me</c> entry in
/// <see cref="barakoCMS.Infrastructure.Security.SecurityHeaders.IsNoStorePath"/>.
/// </para>
/// </remarks>
internal sealed class WhoAmIEndpoint(IQuerySession session, TenantContext tenant) : EndpointWithoutRequest<MeResponse>
{
    public override void Configure()
    {
        Get("/api/me"); // authenticated by default
        Description(b => b
            .Produces<MeResponse>(200)
            .Produces(401)
            .Produces(403));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId)
            || await session.LoadAsync<User>(userId, ct) is not { } user)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var slug = User.FindFirst("tenant")?.Value is { Length: > 0 } claimed
            ? claimed.Trim().ToLowerInvariant()
            : tenant.Slug;

        var roleIds = await MembershipRoles.EffectiveRoleIdsAsync(session, user, slug, ct);
        IReadOnlyList<Role> roles = roleIds.Count == 0
            ? []
            : await session.Query<Role>().Where(r => r.Id.In(roleIds)).ToListAsync(ct);

        var capabilities = roles
            .SelectMany(r => r.SystemCapabilities ?? [])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        await Send.OkAsync(new MeResponse(
            user.Id,
            user.Username,
            slug,
            roles.OrderBy(r => r.Name, StringComparer.Ordinal).Select(r => new MyRole(r.Id, r.Name)).ToList(),
            capabilities), ct);
    }
}

using System.Security.Claims;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Envelope;

/// <summary>
/// Reads <c>X-Branch-Id</c> and makes it the request's branch, once the caller is known to hold it.
/// </summary>
/// <remarks>
/// Runs after authentication. No header sets nothing, and neither does a header on a request that is
/// not signed in. A header that is not an id is 400, and one that
/// is not in the caller's <c>branches</c> profile attribute is 403, so a caller cannot stamp another
/// branch on what they write.
/// </remarks>
internal sealed class EventEnvelopeMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Branch-Id";

    public async Task InvokeAsync(HttpContext context, IEventEnvelopeContext envelope, IBranchMembership membership, IBranchCodes codes)
    {
        if (!context.Request.Headers.TryGetValue(HeaderName, out var raw) || raw.Count == 0)
        {
            await next(context);
            return;
        }

        // Nobody to hold a branch, so nothing to stamp and nothing to refuse. Sign-in and the public
        // routes take the header the same way a client sends it on every request.
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        if (!Guid.TryParse(raw[0], out var branch))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { message = "X-Branch-Id is not a GUID." });
            return;
        }

        var own = await membership.BranchesOfAsync(context.User, context.RequestAborted);
        if (!own.Contains(branch))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { message = "X-Branch-Id is not one of your branches." });
            return;
        }

        using (envelope.Use(branch, await codes.CodeOfAsync(branch, context.RequestAborted)))
        {
            await next(context);
        }
    }
}

/// <summary>The branches the caller holds in the current tenant.</summary>
internal interface IBranchMembership
{
    Task<IReadOnlyList<Guid>> BranchesOfAsync(ClaimsPrincipal user, CancellationToken ct);
}

/// <summary>Reads them from the caller's active membership, the row the roles come from.</summary>
internal sealed class BranchMembership(IQuerySession session, Multitenancy.TenantContext tenant) : IBranchMembership
{
    public async Task<IReadOnlyList<Guid>> BranchesOfAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        if (!Guid.TryParse(user.FindFirst("UserId")?.Value, out var userId))
            return [];

        var stored = await session.LoadAsync<User>(userId, ct);
        if (stored is null)
            return [];

        var (_, membership) = await Multitenancy.MembershipRoles.ResolveAsync(session, stored, tenant.Slug, ct);
        return CallerBranches.Of(membership?.Profile);
    }
}

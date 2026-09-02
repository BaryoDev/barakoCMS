using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Redirects;

internal sealed class ResolveRedirectResponse
{
    public string FromPath { get; init; } = string.Empty;
    public string ToPath { get; init; } = string.Empty;

    /// <summary>301 or 302, so a frontend can pass it straight to its own response.</summary>
    public int Status { get; init; }
}

/// <summary>
/// GET /api/public/redirects/resolve?path=/old. Anonymous, and answers 404 when nothing moved.
/// </summary>
/// <remarks>
/// This runs on the 404 path, which is when a site is already having a bad time, so it is one
/// indexed equality lookup and nothing else. No wildcards, no regular expressions, no scan.
///
/// Anonymous because the caller is a frontend rendering a page for a visitor who has no session. A
/// redirect map is not a secret: every entry in it is a URL that used to be public and a URL that is
/// public now. It is still per tenant, because the document is conjoined and the session carries the
/// tenant, so one site cannot read another's.
///
/// The 404 is deliberately the same shape as any other miss. A frontend asks this exactly once, when
/// its own lookup failed, and an empty 200 would make "no redirect" and "a redirect to nowhere" the
/// same answer.
/// </remarks>
internal sealed class ResolveRedirectEndpoint : EndpointWithoutRequest<ResolveRedirectResponse>
{
    private readonly IQuerySession _session;

    public ResolveRedirectEndpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/public/redirects/resolve");
        AllowAnonymous();

        // Cacheable, because the answer changes only when somebody edits a rule and the caller is
        // asking on a path that is already slow. Short enough that a correction is not stuck for a
        // day, long enough that a crawler hitting a dead section does not ask a thousand times.
        Options(x => x.CacheOutput(p => p.Expire(TimeSpan.FromMinutes(5)).SetVaryByQuery("path")));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var path = UrlRedirect.Normalize(Query<string>("path", isRequired: false));

        if (path == "/")
        {
            // Nothing sensible redirects the root, and treating a missing parameter as a root lookup
            // would answer whatever rule somebody wrote for it.
            await Send.NotFoundAsync(ct);
            return;
        }

        // One equality on the unique per-tenant index. Deliberately not a chain walk: the save path
        // refuses loops and long chains, so a stored rule points where it should, and following a
        // chain here would put the cost of somebody else's mistake on every visitor.
        var redirect = await _session.Query<UrlRedirect>()
            .FirstOrDefaultAsync(r => r.FromPath == path, ct);

        if (redirect is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(new ResolveRedirectResponse
        {
            FromPath = redirect.FromPath,
            ToPath = redirect.ToPath,
            Status = redirect.Permanent ? 301 : 302,
        }, ct);
    }
}

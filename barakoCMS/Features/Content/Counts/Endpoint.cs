using FastEndpoints;
using Marten;
using Marten.Linq.MatchesSql;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace barakoCMS.Features.Content.Counts;

internal sealed class Request
{
    /// <summary>One content type, or null for every type the caller may read.</summary>
    public string? ContentType { get; set; }
}

/// <param name="Total">Every entry counted, in any status.</param>
/// <param name="Draft">Entries in <c>Draft</c>.</param>
/// <param name="Published">Entries in <c>Published</c>.</param>
/// <param name="Archived">Entries in <c>Archived</c>.</param>
/// <param name="Scheduled">Entries in <c>Scheduled</c>, waiting for their publish time.</param>
/// <param name="UncountedTypes">
/// Types the caller may read some of, left out of every number above because their read rules
/// depend on the entry in a way only a per-entry check can answer. Counting those would mean loading the whole
/// type, so they are named instead of guessed. Empty for most callers.
/// </param>
internal sealed record Response(
    int Total,
    int Draft,
    int Published,
    int Archived,
    int Scheduled,
    IReadOnlyList<string> UncountedTypes);

/// <summary>
/// GET /api/contents/counts, how many entries the caller may read, by status, so an overview card
/// can say "121 published, 27 draft" without paging the whole collection.
/// </summary>
/// <remarks>
/// <para>
/// Each type is counted under the read predicate <see cref="IPermissionResolver.ReadPredicateAsync"/>
/// compiles for the caller, the one the list endpoint pages with, so the numbers agree with what
/// <c>GET /api/contents</c> would list. A type whose rules do not compile is named in
/// <c>UncountedTypes</c> rather than loaded row by row: the cost of the answer stays a handful of
/// counts however large the collection is.
/// </para>
/// <para>
/// Types the caller reads unconditionally are counted together, so a caller who reads everything
/// costs four queries whatever the number of types. Across types, the types are the stored content
/// type definitions, so an entry whose type has no definition is not counted.
/// </para>
/// </remarks>
internal sealed class Endpoint(IQuerySession session, IPermissionResolver permissionResolver)
    : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Get("/api/contents/counts"); // authenticated by default
        Description(b => b
            .Produces<Response>(200)
            .Produces(401)
            .Produces(403));
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId)
            || await session.LoadAsync<User>(userId, ct) is not { } user)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var reach = await ReachableContent.ForAsync(
            session, permissionResolver, Resolve<barakoCMS.Infrastructure.Services.IBranchScopeGuard>(),
            User, user, req.ContentType, ct);

        var tally = new Tally();
        foreach (var source in reach.Sources)
        {
            await tally.AddAsync(source, ct);
        }

        await Send.OkAsync(new Response(
            tally.Draft + tally.Published + tally.Archived + tally.Scheduled,
            tally.Draft,
            tally.Published,
            tally.Archived,
            tally.Scheduled,
            reach.Uncounted), ct);
    }

    private sealed class Tally
    {
        public int Draft;
        public int Published;
        public int Archived;
        public int Scheduled;

        public async Task AddAsync(IQueryable<barakoCMS.Models.Content> permitted, CancellationToken ct)
        {
            Draft += await permitted.Where(c => c.Status == ContentStatus.Draft).CountAsync(ct);
            Published += await permitted.Where(c => c.Status == ContentStatus.Published).CountAsync(ct);
            Archived += await permitted.Where(c => c.Status == ContentStatus.Archived).CountAsync(ct);
            Scheduled += await permitted.Where(c => c.Status == ContentStatus.Scheduled).CountAsync(ct);
        }
    }
}

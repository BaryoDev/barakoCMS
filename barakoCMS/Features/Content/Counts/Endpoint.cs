using FastEndpoints;
using Marten;
using Marten.Linq.MatchesSql;
using System.Text.Json.Serialization;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace barakoCMS.Features.Content.Counts;

internal sealed class Request
{
    /// <summary>One content type, or null for every type the caller may read.</summary>
    public string? ContentType { get; set; }

    /// <summary>
    /// Count only entries created in the last <c>24h</c>, <c>7d</c> or <c>30d</c>. Not with
    /// <c>from</c> or <c>to</c>.
    /// </summary>
    public string? Range { get; set; }

    /// <summary>Count only entries created at or after this time. <c>to</c> defaults to now.</summary>
    public DateTimeOffset? From { get; set; }

    /// <summary>Count only entries created before this time. Needs <c>from</c>.</summary>
    public DateTimeOffset? To { get; set; }

    /// <summary>
    /// <c>hour</c> or <c>day</c>: also return <c>series</c>, the window cut into buckets of that
    /// size. Needs a window.
    /// </summary>
    public string? Series { get; set; }
}

/// <summary>The window a windowed answer was counted over.</summary>
/// <param name="From">Inclusive.</param>
/// <param name="To">Exclusive.</param>
internal sealed record WindowInfo(DateTimeOffset From, DateTimeOffset To);

/// <summary>Counts by status, without the list of types left out.</summary>
internal sealed record StatusTotals(int Total, int Draft, int Published, int Archived, int Scheduled);

/// <summary>One bucket of a series: the counts of entries created from <c>Start</c> for the bucket's length.</summary>
internal sealed record SeriesBucket(DateTimeOffset Start, int Total, int Draft, int Published, int Archived, int Scheduled);

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
/// <param name="Window">Only when a window was asked for: the window the numbers above were counted over.</param>
/// <param name="Previous">
/// Only when a window was asked for: the same counts for the window of the same length that ended
/// where this one starts, so a delta is the difference of the two.
/// </param>
/// <param name="Series">
/// Only when <c>series</c> was asked for: one entry per bucket of the window, oldest first, always
/// the same number for the same window, with zero where nothing was created.
/// </param>
internal sealed record Response(
    int Total,
    int Draft,
    int Published,
    int Archived,
    int Scheduled,
    IReadOnlyList<string> UncountedTypes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WindowInfo? Window = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StatusTotals? Previous = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<SeriesBucket>? Series = null);

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
/// <para>
/// With <c>range</c> (or <c>from</c> and <c>to</c>) the entries are counted by <c>createdAt</c> in
/// that window under the same predicates, and the answer carries the window and the previous window
/// of the same length. With <c>series</c> it also carries the buckets. All of that is one more query,
/// over every source at once, whatever the number of types. A window is at most 90 days and a series
/// at most 200 buckets. Without these parameters the answer is the one given before they existed.
/// </para>
/// </remarks>
internal sealed class Endpoint(IQuerySession session, IPermissionResolver permissionResolver, TimeProvider clock)
    : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Get("/api/contents/counts"); // authenticated by default
        Description(b => b
            .Produces<Response>(200)
            .Produces(400)
            .Produces(401)
            .Produces(403));
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        if (await barakoCMS.Infrastructure.Auth.ActingCaller.LoadAsync(session, User, ct) is not { } user)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var reach = await ReachableContent.ForAsync(
            session, permissionResolver, Resolve<barakoCMS.Infrastructure.Services.IBranchScopeGuard>(),
            User, user, req.ContentType, ct);

        var (window, refusal) = CountWindow.Resolve(req.Range, req.From, req.To, req.Series, clock.GetUtcNow());
        if (refusal is not null)
        {
            ThrowError(refusal, 400);
            return;
        }

        if (window is not null)
        {
            await SendWindowedAsync(reach, window, ct);
            return;
        }

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

    private async Task SendWindowedAsync(ContentReach reach, CountWindow window, CancellationToken ct)
    {
        var floor = window.PreviousFrom.UtcDateTime;
        var ceiling = window.To.UtcDateTime;
        IReadOnlyList<IQueryable<barakoCMS.Models.Content>> sources = reach.Sources
            .Select(s => s.Where(c => c.CreatedAt >= floor && c.CreatedAt < ceiling))
            .ToList();

        var cells = await CountWindow.TallyAsync(session, sources, window, ct);

        StatusTotals Totals(Func<ContentStatus, int> of)
        {
            var draft = of(ContentStatus.Draft);
            var published = of(ContentStatus.Published);
            var archived = of(ContentStatus.Archived);
            var scheduled = of(ContentStatus.Scheduled);
            return new StatusTotals(draft + published + archived + scheduled, draft, published, archived, scheduled);
        }

        var current = Totals(s => cells.Current((int)s));
        var series = window.Bucket is null
            ? null
            : Enumerable.Range(0, window.Buckets).Select(i =>
            {
                var b = Totals(s => cells.At(i, (int)s));
                return new SeriesBucket(window.BucketStart(i), b.Total, b.Draft, b.Published, b.Archived, b.Scheduled);
            }).ToList();

        await Send.OkAsync(new Response(
            current.Total, current.Draft, current.Published, current.Archived, current.Scheduled,
            reach.Uncounted,
            new WindowInfo(window.From, window.To),
            Totals(s => cells.Previous((int)s)),
            series), ct);
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

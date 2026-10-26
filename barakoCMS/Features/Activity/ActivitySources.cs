using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Features.Activity.List;
using barakoCMS.Features.WorkflowRuns;
using barakoCMS.Models;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Marten.Linq.MatchesSql;

namespace barakoCMS.Features.Activity;

/// <summary>A position a source consumed, and the row it became, null when it became none.</summary>
/// <remarks>
/// The position counts even without a row: a cursor that stopped at the last row would read the
/// same dropped positions again, and one that skipped them would skip rows behind them.
/// </remarks>
internal sealed record ActivitySlot(ActivityCursor Position, ActivityRow? Row, Guid? ActorUserId = null);

/// <summary>The kinds a request asked for. Null kinds means all of them.</summary>
internal sealed record ActivityWant(IReadOnlySet<string>? Kinds)
{
    public bool Has(string kind) => Kinds is null || Kinds.Contains(kind);
    public bool HasAny(params string[] kinds) => kinds.Any(Has);

    public static ActivityWant Parse(string? kinds)
    {
        if (string.IsNullOrWhiteSpace(kinds)) return new ActivityWant((IReadOnlySet<string>?)null);
        var asked = kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(k => ActivityKinds.All.Contains(k))
            .ToHashSet(StringComparer.Ordinal);
        return new ActivityWant(asked);
    }
}

/// <summary>
/// Reads one source for the feed: up to <c>limit + 1</c> positions after a cursor, oldest first, or
/// the newest ones when there is no cursor. Every read is for the caller's tenant.
/// </summary>
internal static class ActivitySources
{
    private const string AuditTime = "public.mt_immutable_timestamptz(d.data ->> 'CreatedAt')";
    private const string RunTime = "public.mt_immutable_timestamptz(d.data ->> 'UpdatedAt')";
    private const string AuditAfterSql = AuditTime + " >= ? AND (" + AuditTime + ", d.id) > (?, ?)";
    private const string AuditStrictSql = AuditTime + " > ?";
    private const string AuditFromSql = AuditTime + " >= ?";
    private const string RunStrictSql = RunTime + " > ?";
    private const string RunFromSql = RunTime + " >= ?";

    // ---- audit rows: signin, imported, type.changed ----

    public static async Task<List<ActivitySlot>> AuditAsync(
        IQuerySession session, string tenantSlug, ActivityWant want, ActivityCursor? after, int limit, CancellationToken ct)
    {
        var signIn = want.Has(ActivityKinds.SignIn);
        var imported = want.Has(ActivityKinds.Imported);
        var typeChanged = want.Has(ActivityKinds.TypeChanged);
        if (!signIn && !imported && !typeChanged) return [];

        // Fixed text built from constants, never from the request.
        var actions = new List<string>();
        if (signIn) actions.Add("d.data ->> 'Action' = 'auth.login.succeeded'");
        if (imported) actions.Add("d.data ->> 'Action' IN ('redirect.imported', 'tenant.imported')");
        if (typeChanged) actions.Add("d.data ->> 'Action' LIKE 'contenttype.%'");
        var kindsSql = "(" + string.Join(" OR ", actions) + ")";

        // AuditEvent is one table for the deployment, so the tenant is filtered here and not by the session.
        var query = session.Query<AuditEvent>()
            .Where(e => e.TenantSlug == tenantSlug)
            .Where(e => e.MatchesSql(kindsSql));

        List<AuditEvent> rows;
        if (after is { } cursor)
        {
            var at = cursor.At.UtcDateTime;
            query = cursor.Source switch
            {
                ActivitySource.Audit => query.Where(e => e.MatchesSql(AuditAfterSql, at, at, cursor.Id)),
                > ActivitySource.Audit => query.Where(e => e.MatchesSql(AuditStrictSql, at)),
                _ => query.Where(e => e.MatchesSql(AuditFromSql, at)),
            };
            rows = (await query.OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).Take(limit + 1).ToListAsync(ct)).ToList();
        }
        else
        {
            rows = (await query.OrderByDescending(e => e.CreatedAt).Take(limit + 1).ToListAsync(ct)).ToList();
        }

        return rows.Select(AuditSlot).ToList();
    }

    private static ActivitySlot AuditSlot(AuditEvent e)
    {
        // Postgres keeps microseconds and a cursor cut finer than that disagrees with the stored value.
        var ticks = DateTime.SpecifyKind(e.CreatedAt, DateTimeKind.Utc).Ticks;
        var at = new DateTimeOffset(ticks - ticks % 10, TimeSpan.Zero);
        var position = new ActivityCursor(at, ActivitySource.Audit, e.Id, 0);

        var name = string.IsNullOrWhiteSpace(e.ActorUsername) ? null : Clip(e.ActorUsername);
        var actor = e.ActorUserId is not null || name is not null
            ? new ActivityActor { Kind = "user", Name = name ?? "A user" }
            : new ActivityActor();

        string kind;
        ActivitySubject subject;
        string path;
        if (e.Action == "auth.login.succeeded")
        {
            kind = ActivityKinds.SignIn;
            subject = new ActivitySubject
            {
                Kind = "user", Id = e.ActorUserId?.ToString() ?? string.Empty, Title = name ?? "A user",
            };
            path = "/audit?action=auth.login.succeeded";
        }
        else if (e.Action.StartsWith("contenttype.", StringComparison.Ordinal))
        {
            kind = ActivityKinds.TypeChanged;
            var type = Clip(MetadataText(e, "contentType") ?? e.TargetId ?? "A content type");
            subject = new ActivitySubject { Kind = "contentType", Id = type, Title = type };
            path = $"/content-types/{Uri.EscapeDataString(type)}";
        }
        else
        {
            kind = ActivityKinds.Imported;
            subject = new ActivitySubject
            {
                Kind = "import",
                Id = e.Id.ToString("N"),
                Title = e.Action == "redirect.imported" ? "Redirects" : "Tenant bundle",
            };
            path = $"/audit?action={e.Action}";
        }

        return new ActivitySlot(position, new ActivityRow
        {
            Id = $"audit-{e.Id:N}",
            At = at,
            Kind = kind,
            Severity = ActivityKinds.SeverityOf(kind),
            Actor = actor,
            Subject = subject,
            Text = ActivityKinds.TextOf(kind, e.Action),
            Ref = path,
        });
    }

    private static string? MetadataText(AuditEvent e, string key) =>
        e.Metadata is not null && e.Metadata.TryGetValue(key, out var value) && value?.ToString() is { Length: > 0 } text
            ? text
            : null;

    // ---- workflow runs: run.succeeded, run.failed ----

    public static async Task<List<ActivitySlot>> RunsAsync(
        IQuerySession session, ActivityWant want, ActivityCursor? after, int limit, CancellationToken ct)
    {
        var succeeded = want.Has(ActivityKinds.RunSucceeded);
        var failed = want.Has(ActivityKinds.RunFailed);
        if (!succeeded && !failed) return [];

        var query = session.Query<WorkflowRun>().AsQueryable();
        query = (succeeded, failed) switch
        {
            (true, true) => query.Where(r => r.Status == RunStatus.Succeeded
                || r.Status == RunStatus.Failed || r.Status == RunStatus.PartiallyFailed),
            (true, false) => query.Where(r => r.Status == RunStatus.Succeeded),
            _ => query.Where(r => r.Status == RunStatus.Failed || r.Status == RunStatus.PartiallyFailed),
        };

        List<WorkflowRun> runs;
        if (after is { } cursor)
        {
            var at = cursor.At.UtcDateTime;
            query = cursor.Source switch
            {
                ActivitySource.Runs => query.Where(r => r.MatchesSql(RunCursor.AfterSql, at, at, cursor.Id)),
                > ActivitySource.Runs => query.Where(r => r.MatchesSql(RunStrictSql, at)),
                _ => query.Where(r => r.MatchesSql(RunFromSql, at)),
            };
            runs = (await query.OrderBy(r => r.UpdatedAt).ThenBy(r => r.Id).Take(limit + 1).ToListAsync(ct)).ToList();
        }
        else
        {
            runs = (await query.OrderByDescending(r => r.UpdatedAt).Take(limit + 1).ToListAsync(ct)).ToList();
        }

        return runs.Select(RunSlot).ToList();
    }

    private static ActivitySlot RunSlot(WorkflowRun r)
    {
        var position = new ActivityCursor(r.UpdatedAt.ToUniversalTime(), ActivitySource.Runs, r.Id, 0);
        var kind = r.Status == RunStatus.Succeeded ? ActivityKinds.RunSucceeded : ActivityKinds.RunFailed;
        var actor = r.StartedBy is null
            ? new ActivityActor { Kind = "system", Name = "Workflow" }
            : new ActivityActor { Kind = "user", Name = "A user" };

        return new ActivitySlot(position, new ActivityRow
        {
            Id = $"run-{r.Id:N}",
            At = (r.CompletedAt ?? r.UpdatedAt).ToUniversalTime(),
            Kind = kind,
            Severity = ActivityKinds.SeverityOf(kind),
            Actor = actor,
            Subject = new ActivitySubject { Kind = "run", Id = r.Id.ToString(), Title = Clip(r.WorkflowName) },
            Text = ActivityKinds.TextOf(kind),
            Ref = $"/workflows/runs/{r.Id}",
        }, r.StartedBy);
    }

    // ---- the event store: published, scheduled, edited ----

    public static async Task<List<ActivitySlot>> EventsAsync(
        IQuerySession session, ActivityWant want, ActivityCursor? after, int limit,
        Func<barakoCMS.Models.Content, CancellationToken, Task<bool>> mayRead, CancellationToken ct)
    {
        var types = new List<Type>();
        if (want.Has(ActivityKinds.Published)) types.AddRange([typeof(ContentCreated), typeof(ContentStatusChanged)]);
        if (want.Has(ActivityKinds.Scheduled)) types.Add(typeof(ContentScheduled));
        if (want.Has(ActivityKinds.Edited)) types.Add(typeof(ContentUpdated));
        if (types.Count == 0) return [];

        var typeArray = types.ToArray();
        var query = session.Events.QueryAllRawEvents().Where(e => e.EventTypesAre(typeArray));

        IReadOnlyList<IEvent> raw;
        if (after is { } cursor)
        {
            var at = cursor.At;
            var sequence = cursor.Sequence;
            query = cursor.Source switch
            {
                ActivitySource.Events => query.Where(e => e.Timestamp > at || (e.Timestamp == at && e.Sequence > sequence)),
                > ActivitySource.Events => query.Where(e => e.Timestamp > at),
                _ => query.Where(e => e.Timestamp >= at),
            };
            raw = await query.OrderBy(e => e.Timestamp).ThenBy(e => e.Sequence).Take(limit + 1).ToListAsync(ct);
        }
        else
        {
            raw = await query.OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.Sequence).Take(limit + 1).ToListAsync(ct);
        }

        if (raw.Count == 0) return [];

        var ids = raw.Select(e => e.StreamId).Distinct().ToArray();
        var entries = (await session.LoadManyAsync<barakoCMS.Models.Content>(ct, ids)).ToDictionary(c => c.Id);
        var readable = new Dictionary<Guid, bool>();

        var slots = new List<ActivitySlot>(raw.Count);
        foreach (var e in raw)
        {
            var position = new ActivityCursor(e.Timestamp.ToUniversalTime(), ActivitySource.Events, Guid.Empty, e.Sequence);
            var (kind, actor) = Classify(e.Data, want);
            if (kind is null || !entries.TryGetValue(e.StreamId, out var entry))
            {
                slots.Add(new ActivitySlot(position, null));
                continue;
            }

            if (!readable.TryGetValue(entry.Id, out var ok))
            {
                ok = readable[entry.Id] = await mayRead(entry, ct);
            }

            slots.Add(ok
                ? new ActivitySlot(position, new ActivityRow
                {
                    Id = $"event-{e.Sequence}",
                    At = position.At,
                    Kind = kind,
                    Severity = ActivityKinds.SeverityOf(kind),
                    Actor = actor == Guid.Empty ? new ActivityActor() : new ActivityActor { Kind = "user", Name = "A user" },
                    Subject = new ActivitySubject { Kind = "content", Id = entry.Id.ToString(), Title = TitleOf(entry) },
                    Text = ActivityKinds.TextOf(kind),
                    Ref = $"/content/{Uri.EscapeDataString(entry.ContentType)}/{entry.Id}",
                }, actor == Guid.Empty ? null : actor)
                : new ActivitySlot(position, null));
        }

        return slots;
    }

    /// <summary>The kind an event is, or null when it is none the request asked for, and who did it.</summary>
    private static (string? Kind, Guid Actor) Classify(object data, ActivityWant want) => data switch
    {
        ContentCreated { Status: ContentStatus.Published } c when want.Has(ActivityKinds.Published) =>
            (ActivityKinds.Published, c.CreatedBy),
        ContentStatusChanged { NewStatus: ContentStatus.Published } s when want.Has(ActivityKinds.Published) =>
            (ActivityKinds.Published, s.UpdatedBy),
        ContentScheduled { ScheduledPublishAt: not null } s when want.Has(ActivityKinds.Scheduled) =>
            (ActivityKinds.Scheduled, s.UpdatedBy),
        ContentUpdated u when want.Has(ActivityKinds.Edited) => (ActivityKinds.Edited, u.UpdatedBy),
        _ => (null, Guid.Empty),
    };

    private static string TitleOf(barakoCMS.Models.Content entry)
    {
        foreach (var (key, value) in entry.Data)
        {
            if (string.Equals(key, "title", StringComparison.OrdinalIgnoreCase) && value?.ToString() is { Length: > 0 } text)
            {
                return Clip(text);
            }
        }

        return "Untitled";
    }

    // ---- bounces: bounced ----

    public static async Task<List<ActivitySlot>> BouncesAsync(
        IMailBounceSource source, string tenantSlug, ActivityCursor? after, int limit, CancellationToken ct)
    {
        IReadOnlyList<MailBounce> bounces;
        if (after is { } cursor)
        {
            bounces = cursor.Source switch
            {
                ActivitySource.Bounces => await source.ListAsync(tenantSlug, cursor.At, cursor.Id, limit + 1, ct),
                > ActivitySource.Bounces => await source.ListAsync(tenantSlug, cursor.At, null, limit + 1, ct),
                _ => await source.ListAsync(tenantSlug, cursor.At.AddTicks(-1), null, limit + 1, ct),
            };
        }
        else
        {
            bounces = await source.ListNewestAsync(tenantSlug, limit + 1, ct);
        }

        return bounces.Select(b =>
        {
            var at = b.At.ToUniversalTime();
            return new ActivitySlot(new ActivityCursor(at, ActivitySource.Bounces, b.Id, 0), new ActivityRow
            {
                Id = $"bounce-{b.Id:N}",
                At = at,
                Kind = ActivityKinds.Bounced,
                Severity = ActivityKinds.SeverityOf(ActivityKinds.Bounced),
                Actor = new ActivityActor { Kind = "system", Name = "Mail provider" },
                Subject = new ActivitySubject { Kind = "message", Id = b.Id.ToString(), Title = Clip(b.Title ?? "An email") },
                Text = ActivityKinds.TextOf(ActivityKinds.Bounced),
                Ref = "/settings/email",
            });
        }).ToList();
    }

    private static string Clip(string text) => text.Length <= 200 ? text : text[..200];
}

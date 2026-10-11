using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth.Stations;
using barakoCMS.Infrastructure.Realtime;
using FastEndpoints;
using Marten;
using JasperFx.Events;
using Marten.Events;

namespace barakoCMS.Features.Changes;

internal sealed class ChangesRequest
{
    [QueryParam]
    public long After { get; set; }

    [QueryParam]
    public List<string> Topics { get; set; } = [];

    [QueryParam]
    public int? Limit { get; set; }
}

public sealed class ChangesResponse
{
    /// <summary>Pass back as <c>after</c> on the next call.</summary>
    public long Next { get; set; }

    public List<ChangeItem> Items { get; set; } = [];
}

public sealed class ChangeItem
{
    public long Sequence { get; set; }
    public Guid StreamId { get; set; }

    /// <summary>The event's stored alias, for example <c>queue.ticket_issued</c>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Which of the requested topics this event belongs to.</summary>
    public List<string> Topics { get; set; } = [];

    public DateTimeOffset Timestamp { get; set; }
    public object Data { get; set; } = default!;
}

/// <summary>
/// Events after a sequence, for the topics the caller names and holds, in the caller's tenant.
/// </summary>
/// <remarks>
/// Never reads past the high-water mark. A sequence is taken when a transaction writes, not when
/// it commits, so a higher one can be visible while a lower one is still in flight. Reading past
/// the mark would hand a client the higher one, the client would move its cursor past the lower
/// one, and it would never see it. The mark waits for the lower one, so this endpoint does too.
/// </remarks>
internal sealed class ChangesEndpoint(
    IQuerySession session,
    TopicRegistry topics,
    ICallerCapabilities callers,
    IHighWaterMark highWater) : Endpoint<ChangesRequest, ChangesResponse>
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 1000;

    public override void Configure()
    {
        Get("/api/changes");
        Definition.AllowStations();
        Description(b => b.WithTags("Changes"));
        Summary(s => s.Summary = "Events after a sequence for the topics the caller holds.");
    }

    public override async Task HandleAsync(ChangesRequest req, CancellationToken ct)
    {
        if (req.After < 0) AddError(r => r.After, "'after' must be 0 or more.");
        if (req.Topics.Count == 0) AddError(r => r.Topics, "Name at least one topic.");
        ThrowIfAnyErrors();

        var requested = new List<RealtimeTopic>();
        foreach (var name in req.Topics.Distinct(StringComparer.Ordinal))
        {
            var topic = topics.Find(name);
            if (topic is null) AddError(r => r.Topics, $"Unknown topic '{name}'.");
            else requested.Add(topic);
        }
        ThrowIfAnyErrors();

        foreach (var topic in requested)
        {
            if (await callers.HoldsAsync(User, topic.Capability, ct)) continue;
            await Send.StringAsync($"This caller is missing the '{topic.Capability}' capability.", 403, cancellation: ct);
            return;
        }

        var limit = Math.Clamp(req.Limit ?? DefaultLimit, 1, MaxLimit);
        var ceiling = await highWater.CurrentAsync(ct);
        var types = requested.SelectMany(t => t.EventTypes).Distinct().ToArray();

        IReadOnlyList<JasperFx.Events.IEvent> events = ceiling <= req.After
            ? []
            : await session.Events.QueryAllRawEvents()
                .Where(e => e.Sequence > req.After && e.Sequence <= ceiling)
                .Where(e => e.EventTypesAre(types))
                .OrderBy(e => e.Sequence)
                .Take(limit)
                .ToListAsync(ct);

        // A full page may have more behind it, so resume from its last item. Anything shorter
        // means everything up to the ceiling has been read.
        var next = events.Count == limit ? events[^1].Sequence : Math.Max(req.After, ceiling);

        await Send.OkAsync(new ChangesResponse
        {
            Next = next,
            Items = events.Select(e => new ChangeItem
            {
                Sequence = e.Sequence,
                StreamId = e.StreamId,
                Type = e.EventTypeName,
                Topics = requested.Where(t => t.EventTypes.Contains(e.EventType)).Select(t => t.Name).ToList(),
                Timestamp = e.Timestamp,
                Data = e.Data,
            }).ToList(),
        }, ct);
    }
}

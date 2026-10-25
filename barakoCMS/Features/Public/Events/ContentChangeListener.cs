using barakoCMS.Events;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using JasperFx.Events;
using Marten;
using Marten.Services;
using Microsoft.Extensions.Logging;
using ContentDoc = barakoCMS.Models.Content;

namespace barakoCMS.Features.Public.Events;

/// <summary>
/// Turns committed content events into stream events, on the instance that committed them.
/// </summary>
/// <remarks>
/// A Marten session listener rather than a projection. WorkflowProjection learns about the same
/// events from the async daemon, which HotCold pins to one instance so that side effects fire once.
/// A stream hooked in there would only ever reach the subscribers connected to that instance; every
/// other instance's subscribers would hang with no signal. After-commit on the writing session
/// delivers to this instance's own subscribers directly; <see cref="ContentChangeBus"/> carries the
/// change, as an id, to the other instances' subscribers.
///
/// Every payload goes through <see cref="PublicDelivery.ToPublic"/>, the same projection the REST
/// reads use. There is no second copy of the masking rules here, and there must not be: a field the
/// REST read masks is masked here because it is the same function.
///
/// Nothing may escape. This runs inside the caller's SaveChangesAsync after the transaction has
/// committed, and a failure here would report a write that succeeded as one that did not.
/// </remarks>
internal sealed class ContentChangeListener(
    ContentChangeBroadcaster broadcaster,
    ILogger<ContentChangeListener> logger,
    ContentChangeBus? bus = null) : DocumentSessionListenerBase
{
    public override async Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        try
        {
            await BroadcastAsync(session, commit, token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Content event stream failed to broadcast a committed change; the write itself succeeded");
        }
    }

    private async Task BroadcastAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        var events = commit.GetEvents().Where(e => IsContentEvent(e.Data)).ToList();
        if (events.Count == 0)
        {
            return;
        }

        // One session, one tenant. Read from the event rather than the session so the slug is the
        // one the write was recorded under, which is also the key a subscriber registered with.
        var tenant = TenantScopes.SlugFor(events[0].TenantId);
        var local = broadcaster.HasSubscribers(tenant);
        var shared = bus is { Enabled: true };
        if (!local && !shared)
        {
            return;
        }

        var outgoing = await CollectAsync(session, commit, events, token);

        if (local)
        {
            await PublishLocalAsync(session, tenant, outgoing, token);
        }

        if (shared && outgoing.Count > 0)
        {
            await bus!.PublishAsync(
                tenant,
                outgoing.Select(o => new BusChange(o.Id, o.Name)).ToList(),
                token);
        }
    }

    private async Task<List<Outgoing>> CollectAsync(
        IDocumentSession session, IChangeSet commit, List<IEvent> events, CancellationToken token)
    {
        var documents = commit.Updated.Concat(commit.Inserted)
            .OfType<ContentDoc>()
            .GroupBy(c => c.Id)
            .ToDictionary(g => g.Key, g => g.Last());

        var definitions = new Dictionary<string, ContentTypeDefinition?>(StringComparer.Ordinal);
        var outgoing = new List<Outgoing>();

        foreach (var stream in events.GroupBy(e => e.StreamId))
        {
            if (!documents.TryGetValue(stream.Key, out var content))
            {
                content = await session.LoadAsync<ContentDoc>(stream.Key, token);
                if (content is null)
                {
                    continue;
                }
            }

            if (!definitions.TryGetValue(content.ContentType, out var def))
            {
                def = await session.Query<ContentTypeDefinition>()
                    .FirstOrDefaultAsync(d => d.Name == content.ContentType, token);
                definitions[content.ContentType] = def;
            }

            var change = await OutgoingForAsync(session, content, def, stream, token);
            if (change is not null)
            {
                outgoing.Add(change);
            }
        }

        return outgoing;
    }

    private static async Task<Outgoing?> OutgoingForAsync(
        IDocumentSession session,
        ContentDoc content,
        ContentTypeDefinition? def,
        IGrouping<Guid, IEvent> stream,
        CancellationToken token)
    {
        if (!PublicDelivery.IsDeliverable(def))
        {
            return null;
        }

        var declared = DeclaredSensitivity(stream);
        var effective = WithSensitivity(def!, declared);
        var slugField = PublicDelivery.SlugField(effective);
        var projected = Project(content, effective);

        if (projected is not null)
        {
            var name = stream.Any(e => BecamePublic(e.Data))
                ? ContentChangeEvents.Published
                : ContentChangeEvents.Updated;
            return new Outgoing(content.Id, name, projected, effective, null, declared);
        }

        // Not public now. Worth an event only if it was public before this commit; a draft
        // moving to Archived was never on anybody's site, and an unpublish for it would hand
        // out the slug of an entry the REST API answers 404 for.
        if (stream.Any(e => LeftPublic(e.Data))
            && await WasPublicBeforeAsync(session, stream.Key, stream, def!, slugField, token))
        {
            return new Outgoing(content.Id, ContentChangeEvents.Unpublished, null, null, Unpublish(content, slugField), declared);
        }

        return null;
    }

    /// <summary>The unpublish for an entry: its identity and nothing else.</summary>
    internal static ContentChange Unpublish(ContentDoc content, string? slugField) => new(
        ContentChangeEvents.Unpublished,
        content.Id,
        content.ContentType,
        PublicDelivery.SlugValue(content, slugField),
        new UnpublishedPayload(content.Id, content.ContentType, PublicDelivery.SlugValue(content, slugField)));

    /// <summary>
    /// The anonymous projection of an entry, or null when it is not deliverable. The stream reads
    /// no file store from inside a commit, so a file field is left out rather than sent as an id
    /// the delivery routes would not answer.
    /// </summary>
    internal static PublicContentResponse? Project(ContentDoc content, ContentTypeDefinition effective)
    {
        var projected = PublicDelivery.ToPublic(content, effective, PublicDelivery.SlugField(effective));
        return projected is null ? null : PublicFileFields.LeaveOut(projected, effective);
    }

    private async Task PublishLocalAsync(
        IDocumentSession session, string tenant, List<Outgoing> outgoing, CancellationToken token)
    {
        // References are checked for every entry in the commit together, in one read, and the
        // changes go out in the order they were found.
        var entries = outgoing
            .Where(o => o.Projected is not null)
            .Select(o => (Item: o.Projected!, Definition: o.Definition!))
            .ToList();
        var checkedEntries = new Queue<PublicContentResponse>(await CheckReferencesAsync(session, entries, logger, token));

        foreach (var change in outgoing)
        {
            if (change.Ready is not null)
            {
                broadcaster.Publish(tenant, change.Ready);
                continue;
            }

            var entry = checkedEntries.Dequeue();
            broadcaster.Publish(tenant, new ContentChange(change.Name, entry.Id, entry.ContentType, entry.Slug, entry));
        }
    }

    /// <summary>
    /// The entries with their references checked, or, when the check cannot be made, with every
    /// reference field left out.
    /// </summary>
    /// <remarks>
    /// A failed read must not cost the commit its other changes: an unpublish in the same commit is
    /// sent either way, and so is each entry, without the ids nothing could vouch for.
    /// </remarks>
    internal static async Task<List<PublicContentResponse>> CheckReferencesAsync(
        IDocumentSession session,
        List<(PublicContentResponse Item, ContentTypeDefinition Definition)> entries,
        ILogger logger,
        CancellationToken token)
    {
        try
        {
            return await PublicReferenceFields.FilterAsync(entries, session, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Content event stream could not check references, so they are left out of this commit's payloads");
            return entries.Select(e => PublicReferenceFields.LeaveOut(e.Item, e.Definition)).ToList();
        }
    }

    /// <summary>A change to send: an entry still to have its references checked, or a ready unpublish.</summary>
    private sealed record Outgoing(
        Guid Id,
        string Name,
        PublicContentResponse? Projected,
        ContentTypeDefinition? Definition,
        ContentChange? Ready,
        Dictionary<string, SensitivityLevel>? Declared);

    /// <summary>
    /// Folds the stream as it stood before this commit and asks the same projection whether that
    /// state was deliverable.
    /// </summary>
    /// <remarks>
    /// Only reached for a status or sensitivity change away from public, with a subscriber on the
    /// tenant, so the extra read is paid on the rare path. A stream whose versions are not known
    /// answers yes: a spurious unpublish costs a subscriber one lookup, a missing one leaves a
    /// stale page up.
    /// </remarks>
    private static async Task<bool> WasPublicBeforeAsync(
        IDocumentSession session,
        Guid id,
        IEnumerable<IEvent> committed,
        ContentTypeDefinition def,
        string? slugField,
        CancellationToken token)
    {
        var firstVersion = committed.Min(e => e.Version);
        if (firstVersion <= 0)
        {
            return true;
        }

        if (firstVersion == 1)
        {
            return false;
        }

        var before = await session.Events.FetchStreamAsync(id, version: firstVersion - 1, token: token);
        var prior = ContentProjection.Fold(before);
        return prior is not null && PublicDelivery.ToPublic(prior, def, slugField) is not null;
    }

    /// <summary>
    /// The definition as the commit says it stands. A field's sensitivity change is appended to each
    /// entry before the type's own write lands (SetFieldSensitivity scrubs search text first, so a
    /// failure part way leaves the field readable rather than in anonymous search), and the
    /// definition read here still says Public. Projecting with the sensitivity the event declares
    /// keeps ToPublic the only masking rule. A copy, so the session's instance is not touched.
    /// </summary>
    internal static Dictionary<string, SensitivityLevel>? DeclaredSensitivity(IEnumerable<IEvent> committed)
    {
        var declared = new Dictionary<string, SensitivityLevel>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in committed.Select(e => e.Data).OfType<ContentFieldSensitivityChanged>())
        {
            declared[change.Field] = change.To;
        }

        return declared.Count == 0 ? null : declared;
    }

    /// <summary>The definition with the declared sensitivities applied, or itself when there are none.</summary>
    internal static ContentTypeDefinition WithSensitivity(
        ContentTypeDefinition def, IReadOnlyDictionary<string, SensitivityLevel>? declared)
    {
        if (declared is null || declared.Count == 0)
        {
            return def;
        }

        return new ContentTypeDefinition
        {
            Id = def.Id,
            Name = def.Name,
            DisplayName = def.DisplayName,
            Description = def.Description,
            IsPubliclyDeliverable = def.IsPubliclyDeliverable,
            Icon = def.Icon,
            RouteTemplate = def.RouteTemplate,
            StructuredDataType = def.StructuredDataType,
            CreatedAt = def.CreatedAt,
            UpdatedAt = def.UpdatedAt,
            Lifecycle = def.Lifecycle,
            Fields = def.Fields.Select(f => declared.TryGetValue(f.Name, out var to)
                ? WithSensitivity(f, to)
                : f).ToList(),
        };
    }

    /// <summary>A copy of the field with another sensitivity and every other member as it was.</summary>
    /// <remarks>
    /// Member by member, so a property added to <see cref="FieldDefinition"/> has to be added here.
    /// <c>FieldDefinitionCopyTests</c> fails when one is not.
    /// </remarks>
    internal static FieldDefinition WithSensitivity(FieldDefinition f, SensitivityLevel to) => new()
    {
        Name = f.Name,
        DisplayName = f.DisplayName,
        Type = f.Type,
        ReferenceType = f.ReferenceType,
        Options = f.Options,
        Multiple = f.Multiple,
        ItemType = f.ItemType,
        JsonSchema = f.JsonSchema,
        Parent = f.Parent,
        Currency = f.Currency,
        Scale = f.Scale,
        Editor = f.Editor,
        Section = f.Section,
        Role = f.Role,
        TokenLength = f.TokenLength,
        Computed = f.Computed,
        IsRequired = f.IsRequired,
        DefaultValue = f.DefaultValue,
        ValidationRules = f.ValidationRules,
        Sensitivity = to,
        VisibleToRoles = f.VisibleToRoles,
        Mask = f.Mask,
    };

    private static bool IsContentEvent(object data) => data is
        ContentCreated or ContentUpdated or ContentStatusChanged or ContentTransitioned
        or ContentSensitivityChanged or ContentFieldSensitivityChanged or ContentFieldChanged;

    private static bool BecamePublic(object data) => data is
        ContentCreated
        or ContentStatusChanged { NewStatus: ContentStatus.Published }
        or ContentSensitivityChanged { Sensitivity: SensitivityLevel.Public };

    private static bool LeftPublic(object data) => data is
        ContentStatusChanged { NewStatus: not ContentStatus.Published }
        or ContentSensitivityChanged { Sensitivity: not SensitivityLevel.Public };
}

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
/// gives each instance its own writes, which is the limitation docs/delivery-api.md states.
///
/// Every payload goes through <see cref="PublicDelivery.ToPublic"/>, the same projection the REST
/// reads use. There is no second copy of the masking rules here, and there must not be: a field the
/// REST read masks is masked here because it is the same function.
///
/// Nothing may escape. This runs inside the caller's SaveChangesAsync after the transaction has
/// committed, and a failure here would report a write that succeeded as one that did not.
/// </remarks>
internal sealed class ContentChangeListener : DocumentSessionListenerBase
{
    private readonly ContentChangeBroadcaster _broadcaster;
    private readonly ILogger<ContentChangeListener> _logger;

    public ContentChangeListener(ContentChangeBroadcaster broadcaster, ILogger<ContentChangeListener> logger)
    {
        _broadcaster = broadcaster;
        _logger = logger;
    }

    public override async Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        try
        {
            await BroadcastAsync(session, commit, token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Content event stream failed to broadcast a committed change; the write itself succeeded");
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
        if (!_broadcaster.HasSubscribers(tenant))
        {
            return;
        }

        var documents = commit.Updated.Concat(commit.Inserted)
            .OfType<ContentDoc>()
            .GroupBy(c => c.Id)
            .ToDictionary(g => g.Key, g => g.Last());

        var definitions = new Dictionary<string, ContentTypeDefinition?>(StringComparer.Ordinal);

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

            if (!PublicDelivery.IsDeliverable(def))
            {
                continue;
            }

            var effective = WithDeclaredSensitivity(def!, stream);
            var slugField = PublicDelivery.SlugField(effective);
            var projected = PublicDelivery.ToPublic(content, effective, slugField);

            if (projected is not null)
            {
                var name = stream.Any(e => BecamePublic(e.Data))
                    ? ContentChangeEvents.Published
                    : ContentChangeEvents.Updated;
                _broadcaster.Publish(tenant, new ContentChange(
                    name, projected.Id, projected.ContentType, projected.Slug, projected));
                continue;
            }

            // Not public now. Worth an event only if it was public before this commit; a draft
            // moving to Archived was never on anybody's site, and an unpublish for it would hand
            // out the slug of an entry the REST API answers 404 for.
            if (stream.Any(e => LeftPublic(e.Data))
                && await WasPublicBeforeAsync(session, stream.Key, stream, def!, slugField, token))
            {
                _broadcaster.Publish(tenant, new ContentChange(
                    ContentChangeEvents.Unpublished,
                    content.Id,
                    content.ContentType,
                    PublicDelivery.SlugValue(content, slugField),
                    new UnpublishedPayload(content.Id, content.ContentType, PublicDelivery.SlugValue(content, slugField))));
            }
        }
    }

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
    private static ContentTypeDefinition WithDeclaredSensitivity(ContentTypeDefinition def, IEnumerable<IEvent> committed)
    {
        var declared = new Dictionary<string, SensitivityLevel>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in committed.Select(e => e.Data).OfType<ContentFieldSensitivityChanged>())
        {
            declared[change.Field] = change.To;
        }

        if (declared.Count == 0)
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
            CreatedAt = def.CreatedAt,
            UpdatedAt = def.UpdatedAt,
            Lifecycle = def.Lifecycle,
            Fields = def.Fields.Select(f => declared.TryGetValue(f.Name, out var to)
                ? new FieldDefinition
                {
                    Name = f.Name,
                    DisplayName = f.DisplayName,
                    Type = f.Type,
                    ReferenceType = f.ReferenceType,
                    IsRequired = f.IsRequired,
                    DefaultValue = f.DefaultValue,
                    ValidationRules = f.ValidationRules,
                    Sensitivity = to,
                    VisibleToRoles = f.VisibleToRoles,
                    Mask = f.Mask,
                }
                : f).ToList(),
        };
    }

    private static bool IsContentEvent(object data) => data is
        ContentCreated or ContentUpdated or ContentStatusChanged or ContentTransitioned
        or ContentSensitivityChanged or ContentFieldSensitivityChanged;

    private static bool BecamePublic(object data) => data is
        ContentCreated
        or ContentStatusChanged { NewStatus: ContentStatus.Published }
        or ContentSensitivityChanged { Sensitivity: SensitivityLevel.Public };

    private static bool LeftPublic(object data) => data is
        ContentStatusChanged { NewStatus: not ContentStatus.Published }
        or ContentSensitivityChanged { Sensitivity: not SensitivityLevel.Public };
}

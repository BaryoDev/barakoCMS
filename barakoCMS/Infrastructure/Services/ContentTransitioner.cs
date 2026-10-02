using System.Security.Claims;
using barakoCMS.Core.Interfaces;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Logging;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Performs a named transition against the content type's own lifecycle.
/// </summary>
/// <remarks>
/// The refusal is server side, not a button the admin declines to draw. CLAUDE.md section 9 is
/// explicit that hiding a control is not access control, and a lifecycle that only the UI
/// enforces is a lifecycle any HTTP client can ignore.
///
/// ContentStatus is untouched here. The enum decides whether public delivery serves an entry and
/// a custom lifecycle decides nothing about that, so an invoice moving from Submitted to Approved
/// does not become publicly visible as a side effect. Conflating the two is the shortcut that
/// makes a system nobody can explain.
///
/// Lifecycle:EnforceTransitions exists because a deployment adopting lifecycles has entries that
/// predate the rules, and refusing every edit to them is not a migration path. Off logs the
/// violation and allows it, which is a deliberate escape hatch rather than an oversight, and it
/// defaults to on.
///
/// A transition may declare fields. The ones it requires must be sent in Data with the move, a
/// value already on the entry does not count, and Data may carry only the fields the transition
/// declares. The actor needs the transition permission and not Update to send them: a reviewer
/// who may not edit an entry still has to say why they rejected it, and the declared list is
/// what keeps that from becoming a general edit. Sent values go through the gates an update
/// runs (field sensitivity, the type's validation, the before-save hooks) and are recorded as a
/// ContentUpdated beside the ContentTransitioned, in one commit.
///
/// The order of the checks is part of the behaviour. Each refusal says something about the type
/// or the entry, so each one sits behind the check that decides whether the actor may be told.
/// </remarks>
internal sealed class ContentTransitioner(
    IDocumentSession session,
    IPermissionResolver permissionResolver,
    TenantContext tenant,
    IContentWriter contentWriter,
    IContentSourcingPolicy sourcing,
    IConfiguration configuration,
    IHttpContextAccessor httpContextAccessor,
    IServiceProvider services,
    ILogger<ContentTransitioner> logger) : IContentTransitioner
{
    internal const string ChangedByAnotherWriter =
        "The content was changed by another writer. Please refresh and try again.";

    public async Task<ContentTransitionResult> TransitionAsync(
        Content content,
        string transition,
        ContentTransitionActor actor,
        ContentTransitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(actor);

        var ct = cancellationToken;
        var skipPermissionChecks = options?.SkipPermissionChecks ?? false;

        // A user actor has to be a user, whatever the caller skipped. The events and the audit row
        // name this id, and an id nobody holds would be an actor that never existed.
        User? user = null;
        if (actor.UserId is { } actorUserId)
        {
            user = await session.LoadAsync<User>(actorUserId, ct);
            if (user is null)
            {
                return ContentTransitionResult.Forbidden("The actor is not a user.");
            }
        }
        else if (!skipPermissionChecks)
        {
            return ContentTransitionResult.Forbidden(
                "A system actor holds no permissions. Set SkipPermissionChecks to make the move on the caller's own authority.");
        }

        // Inside the actor's own request for this tenant, the token is what says the user may act
        // here, and nothing more is asked. Anywhere else no token was presented, so the rule a
        // token is issued under is applied: global roles do not let a user act in a registered
        // tenant they hold no active membership in.
        var ownRequest = user is null ? null : OwnRequest(user);

        if (user is not null
            && ownRequest is null
            && !skipPermissionChecks
            && await TenantAccessDenialAsync(user, ct) is { } denial)
        {
            return ContentTransitionResult.Forbidden(denial);
        }

        var definition = await session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == content.ContentType, ct);

        // Nothing below this line may be reached by an actor with no rights on the type. The
        // refusals further down name the type's declared transitions and the entry's current
        // lifecycle state, which is a workflow map handed to anyone holding a valid token. Read is
        // the floor rather than Update, because a manager approves an amount they may not edit.
        if (!skipPermissionChecks
            && !await permissionResolver.CanPerformActionAsync(user!, content.ContentType, "read", content, ct))
        {
            return ContentTransitionResult.Forbidden("The actor may not read entries of this content type.");
        }

        var lifecycle = definition?.Lifecycle;
        if (definition is null || lifecycle is null)
        {
            return ContentTransitionResult.Invalid(
                [$"Content type '{content.ContentType}' declares no lifecycle, so it has no transitions."]);
        }

        // An entry written before the type declared a lifecycle has no state. It starts at the
        // declared initial state rather than being unmovable, because the alternative is content
        // that can never be transitioned and no way to fix it short of editing the database.
        var currentState = content.LifecycleState ?? lifecycle.InitialState;

        var declared = lifecycle.Transitions.FirstOrDefault(
            t => string.Equals(t.Name, transition, StringComparison.OrdinalIgnoreCase));

        if (declared is null)
        {
            var available = lifecycle.Transitions.Count == 0
                ? "(none)"
                : string.Join(", ", lifecycle.Transitions.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));
            return ContentTransitionResult.Invalid(
                [$"'{transition}' is not a transition on '{content.ContentType}'. Declared transitions: {available}."]);
        }

        // Checked here rather than with the read check, because which permission applies depends
        // on which transition was named. It runs before the state check below, so an actor who may
        // not perform a transition is not told which state the entry is sitting in.
        //
        // A transition permission is not implied by Update. Falling back to the Update rule is the
        // obvious way to keep existing configurations working and it grants approval to everyone
        // who can edit. Undeclared means refused.
        if (!skipPermissionChecks
            && !await permissionResolver.CanPerformActionAsync(
                user!, content.ContentType, PermissionResolver.TransitionActionPrefix + declared.Name, content, ct))
        {
            return ContentTransitionResult.Forbidden("The actor does not hold the permission for this transition.");
        }

        // Whether the person who raised a record may move it on is a policy, not a bug, and
        // organisations answer it differently. Refused by default because that is the direction that
        // can be relaxed later: granting it and tightening afterwards takes away something people
        // were relying on, and an approval that should not have happened cannot be undone.
        //
        // CreatedBy is what this reads, not LastModifiedBy, which moves to whoever edited last and
        // would make the check mean nothing after any edit.
        //
        // A policy about who acted and not a permission, so skipping the permission checks does not
        // skip it. It is a rule about people: a system actor raised nothing, and every entry no
        // user created carries the same empty CreatedBy, so comparing against it would refuse the
        // system on exactly those entries.
        if (user is not null
            && content.CreatedBy == user.Id
            && !configuration.GetValue($"Lifecycle:AllowSelfTransition:{declared.Name}", false))
        {
            logger.LogInformation(
                "Refused a self transition of {ContentId} by its creator. Set Lifecycle:AllowSelfTransition:{Transition} to allow it.",
                content.Id, declared.Name);

            return ContentTransitionResult.Forbidden("The actor created this entry and may not move it on.");
        }

        if (!string.Equals(declared.From, currentState, StringComparison.OrdinalIgnoreCase))
        {
            var enforce = configuration.GetValue("Lifecycle:EnforceTransitions", true);
            var message = $"'{declared.Name}' moves {declared.From} to {declared.To}, and this entry is {currentState}.";

            if (enforce)
            {
                return ContentTransitionResult.Conflict(message);
            }

            // Recorded at warning level rather than passed over. The setting exists to let existing
            // data through, and an operator who turned it on should be able to see what it let
            // through and how often.
            logger.LogWarning(
                "Lifecycle:EnforceTransitions is off and permitted an out-of-order transition on {ContentId}: {Message}",
                content.Id, message);
        }

        // After every permission check above, so an actor who may not make this move learns nothing
        // about the fields it asks for.
        var takes = TransitionFields.Resolve(declared, definition);

        if (takes.Skipped.Count > 0)
        {
            logger.LogWarning(
                "Transition {Transition} on content type {ContentType} names {Count} field(s) the type does not declare, so they were skipped: {Fields}",
                LogSafe.Value(declared.Name), LogSafe.Value(content.ContentType), takes.Skipped.Count,
                LogSafe.Value(string.Join(", ", takes.Skipped)));
        }

        var errors = new List<string>();
        Dictionary<string, object>? data = null;
        var sentValues = new Dictionary<FieldDefinition, object?>();
        long? streamVersion = null;
        Guid? documentVersion = null;

        // The version first, the entry second, for every move and not only one that sends data.
        //
        // The checks above read the caller's copy, which is as old as the caller made it: loaded at
        // the top of a request, or by a module before it called somewhere else. The writer rebuilds
        // the document on what is stored and the transition event sets the state whatever it was,
        // so a move checked against a stale Draft would be applied to an entry that has since
        // become Approved, and answered as a success. Reading the entry again and comparing its
        // state is what refuses that.
        //
        // Sent values are laid over a copy of the whole bag for the same reason: a bag read before
        // another writer's write would put that writer's fields back as they were.
        //
        // Reading the version before the entry means a write before this point is in the copy, and
        // a write after it fails this one. An event-sourced type is guarded by its stream version,
        // which the writer binds to the append. Every other type is guarded by the document's own
        // version, bound below.
        if (await sourcing.IsEventSourcedAsync(content.ContentType, ct))
        {
            streamVersion = (await session.Events.FetchStreamStateAsync(content.Id, ct))?.Version ?? 0;
        }
        else
        {
            documentVersion = (await session.MetadataForAsync(content, ct))?.CurrentVersion;
        }

        var current = await session.LoadAsync<Content>(content.Id, ct);

        if (current is null
            || !string.Equals(current.LifecycleState, content.LifecycleState, StringComparison.Ordinal))
        {
            return ContentTransitionResult.Conflict(ChangedByAnotherWriter);
        }

        IReadOnlyDictionary<string, object> stored = current.Data;

        if (takes.TakesFields && options?.Data is { Count: > 0 } sent)
        {
            data = new Dictionary<string, object>(current.Data, current.Data.Comparer);

            var notTaken = 0;

            foreach (var (key, value) in sent)
            {
                var field = takes.Find(key);
                if (field is null)
                {
                    notTaken++;
                    continue;
                }

                if (!sentValues.TryAdd(field, value))
                {
                    errors.Add($"Field '{field.DisplayName}' ({field.Name}) was sent more than once, ignoring case.");
                    continue;
                }

                // Under the key the entry already stores it as, which is the one the validator reads.
                var storedKey = data.Keys.FirstOrDefault(k => TransitionFields.Matches(k, field.Name)) ?? field.Name;
                data[storedKey] = value;
            }

            // Counted and not named: the keys are whatever the caller typed.
            if (notTaken > 0)
            {
                errors.Add($"'{declared.Name}' takes {string.Join(", ", takes.Names)} in data, and "
                    + $"{notTaken} other {(notTaken == 1 ? "field was" : "fields were")} sent.");
            }

            if (errors.Count > 0)
            {
                return ContentTransitionResult.Invalid(errors);
            }

            // An actor who may not see a field may not change it. Reverts any such field to what is
            // stored, before the required check reads it.
            if (!skipPermissionChecks)
            {
                await services.GetRequiredService<ISensitivityService>().ApplyWriteAsync(
                    content.ContentType, data, stored, ownRequest ?? RequestNaming(user!), ct);
            }
        }

        foreach (var field in TransitionFields.NotSent(takes, data, sentValues))
        {
            errors.Add($"Field '{field.DisplayName}' ({field.Name}) is required by the transition '{declared.Name}'.");
        }

        if (errors.Count > 0)
        {
            return ContentTransitionResult.Invalid(errors);
        }

        var recordedBy = actor.RecordedId;
        var events = new List<object>();

        if (data is not null)
        {
            var validation = await services.GetRequiredService<IContentValidatorService>()
                .ValidateAsync(
                    content.ContentType,
                    data,
                    // The entry as stored now, which the data was built from, not the caller's copy.
                    existing: current,
                    // A file is checked for the actor, never for whoever made the request; a system
                    // actor names nobody and takes public files only.
                    caller: user is null ? null : (ownRequest ?? RequestNaming(user)).User);
            if (!validation.IsValid && validation.Errors.Count > 0)
            {
                return ContentTransitionResult.Invalid(validation.Errors);
            }

            var hookErrors = await services.GetRequiredService<IContentLifecycleRunner>()
                .RunBeforeSaveAsync(content.ContentType, content.Id, data, stored, recordedBy, ct);
            if (hookErrors.Count > 0)
            {
                return ContentTransitionResult.Invalid(hookErrors);
            }

            var publicFields = definition.Fields
                .Where(f => f.Sensitivity == SensitivityLevel.Public)
                .Select(f => f.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var searchText = string.Join(
                ' ',
                data
                    .Where(kv => publicFields.Contains(kv.Key))
                    .Select(kv => kv.Value?.ToString())
                    .Where(v => !string.IsNullOrWhiteSpace(v)));

            events.Add(new Events.ContentUpdated(content.Id, data, recordedBy, searchText, DateTime.UtcNow));
        }

        events.Add(new Events.ContentTransitioned(
            content.Id, declared.Name, currentState, declared.To, recordedBy, DateTime.UtcNow));

        // The row names a user only when a user made the move. A system actor has no user id and no
        // username, so both stay empty and the name it gave goes in the metadata.
        var metadata = new Dictionary<string, object>
        {
            ["transition"] = declared.Name,
            ["from"] = currentState,
            ["to"] = declared.To,
        };
        if (actor.SystemName is { } systemName)
        {
            metadata["actor"] = $"system:{systemName}";
        }

        if (skipPermissionChecks)
        {
            metadata["permissionChecks"] = "skipped";
        }

        try
        {
            await contentWriter.AppendAsync(content, events, streamVersion, ct);

            // After the append and before the commit: the writer loads the document again to
            // store it, and a version bound before that load is discarded.
            if (documentVersion is { } expected)
            {
                session.UpdateExpectedVersion(content, expected);
            }

            await AuditLog.RecordAsync(session, tenant.Slug, "content.transitioned", actor.UserId, user?.Username,
                targetType: content.ContentType, targetId: content.Id.ToString(),
                metadata: metadata,
                ct: ct);

            await session.SaveChangesAsync(ct);
        }
        catch (StaleContentException)
        {
            return ContentTransitionResult.Conflict(ChangedByAnotherWriter);
        }
        catch (Exception ex) when (ex is JasperFx.ConcurrencyException
            || ex.GetType().Name.Contains("Concurrency")
            || ex.GetType().Name.Contains("UnexpectedMaxEventId"))
        {
            return ContentTransitionResult.Conflict(ChangedByAnotherWriter);
        }

        return ContentTransitionResult.Transitioned(declared.Name, currentState, declared.To);
    }

    /// <summary>
    /// The current request, when the actor is its caller and it was resolved to this scope's tenant.
    /// </summary>
    /// <remarks>
    /// Both halves matter. A request made by somebody else says nothing about the actor. A request
    /// by the actor for another tenant carries a token issued for that tenant, and it is no proof
    /// that the user may hold one for this tenant, so code that opens a scope for this tenant from
    /// inside it is asked for the membership like any other caller outside a request.
    /// </remarks>
    private HttpContext? OwnRequest(User user)
    {
        var request = httpContextAccessor.HttpContext;
        if (request is null
            || !Guid.TryParse(request.User.FindFirst("UserId")?.Value, out var caller)
            || caller != user.Id)
        {
            return null;
        }

        var requestTenant = request.RequestServices?.GetService<TenantContext>();
        return requestTenant is not null && string.Equals(requestTenant.Slug, tenant.Slug, StringComparison.Ordinal)
            ? request
            : null;
    }

    /// <summary>
    /// Null when the user could hold a token for this scope's tenant, otherwise why not.
    /// </summary>
    /// <remarks>
    /// The rule a token is issued under: the default tenant and a slug nobody registered have no
    /// membership model, and a registered tenant has to be active and the user an active member.
    /// </remarks>
    private async Task<string?> TenantAccessDenialAsync(User user, CancellationToken ct)
    {
        if (tenant.IsDefault)
        {
            return null;
        }

        var slug = tenant.Slug;
        var userId = user.Id;

        var registered = await session.Query<Tenant>().FirstOrDefaultAsync(t => t.Slug == slug, ct);
        if (registered is null)
        {
            return null;
        }

        if (!registered.IsActive)
        {
            return "The tenant is inactive.";
        }

        var isMember = await session.Query<Membership>()
            .AnyAsync(m => m.UserId == userId && m.TenantSlug == slug && m.Status == MembershipStatus.Active, ct);

        return isMember ? null : "The actor has no active membership in this tenant.";
    }

    /// <summary>
    /// A request to answer field sensitivity for, when the actor has none of their own here.
    /// </summary>
    /// <remarks>
    /// The sensitivity service takes an HttpContext and reads one thing from it: the UserId claim,
    /// from which it looks up the roles and capabilities the user holds in this scope's tenant. So
    /// the context made here carries that claim and nothing else. It states no role and no
    /// capability, because none it stated would be read, and one that was read would be an answer
    /// this class made up.
    /// </remarks>
    private HttpContext RequestNaming(User user)
    {
        var identity = new ClaimsIdentity(new[] { new Claim("UserId", user.Id.ToString()) }, nameof(ContentTransitioner));
        return new DefaultHttpContext { User = new ClaimsPrincipal(identity), RequestServices = services };
    }
}

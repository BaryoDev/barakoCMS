using System.Text.Json;
using barakoCMS.Core.Validation;
using barakoCMS.Features.Content;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Infrastructure.OpenApi;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.ContentType.SetLifecycle;

/// <summary>
/// PUT /api/content-types/{name}/lifecycle, which replaces the states, the initial state and the
/// transitions of a type that already exists.
/// </summary>
/// <remarks>
/// Its own endpoint because content types have no general update, and a config apply refuses a changed
/// lifecycle. No entry is read into memory or rewritten, so every reader
/// follows the new lifecycle at once, and an edit that would leave something pointing at a state or
/// a transition that is gone is refused instead: 409, with every blocker listed, each with its kind,
/// the state or transition it is about, what blocks it and a count where it has one. There is no
/// <c>force</c> and no mapping of entries to another state. Move the entries with a transition
/// first.
///
/// The type's lock is taken exclusive before anything is read. A move in flight holds it shared, so
/// the edit waits for it and a move that starts after it sees the new lifecycle, and two edits
/// cannot interleave. The counts are a reading taken before the save, so the type is read again
/// after them and the edit is refused if its lifecycle is no longer the one counted against. A dry
/// run runs the same checks and answers 200 with the blockers, stored nothing.
///
/// A save sends the version it started from as <c>If-Match</c>, the way an entry update does: the
/// type's stored document version as a quoted ETag, 428 when it is missing, 400 when it is not a
/// version, 412 when it is stale. The version is read under the lock, so two edits that started
/// from the same version cannot both be saved. A dry run needs none, and when it sends one it is
/// checked the same way.
/// </remarks>
internal class Endpoint(
    IDocumentSession session,
    IContentTypeValidatorService validator,
    LifecycleEditChecks checks,
    DeliveryDocumentCache openApiCache,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Put("/api/content-types/{name}/lifecycle");
        // What states an entry may be in is modelling, the same gate as creating the type.
        Definition.RequireCapability(SystemCapabilities.ManageContentTypes, "SuperAdmin", "Admin");
        Definition.RefusesWhileFieldChangeRuns();
        Description(b => b.Produces<Refusal>(409).Produces<StaleRefusal>(412));
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var name = barakoCMS.Core.ContentTypeName.Normalize(Route<string>("name") ?? string.Empty);

        if (req.Lifecycle is null)
        {
            AddError("lifecycle is required. An edit replaces the lifecycle and does not remove it.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var (answered, claimed) = await ReadIfMatchAsync(req, ct);
        if (answered)
        {
            return;
        }

        // Before the type is read, so a move that committed in between is counted and one in flight is waited for.
        await FieldChanges.LockTypeAsync(session, name, ct);

        var def = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name.ToLower() == name, ct);
        if (def is null)
        {
            await NotFoundOrAccountOwnedAsync(name, ct);
            return;
        }

        if (await GateAsync(def, claimed, ct) is not { } version
            || !await ValidAsync(req.Lifecycle, def, ct))
        {
            return;
        }

        var changed = !Same(def.Lifecycle, req.Lifecycle);
        var check = changed
            ? await checks.CheckAsync(session, def, req.Lifecycle, ct)
            : new LifecycleCheck([], []);

        if (req.DryRun)
        {
            await Send.OkAsync(Answer(def, req.Lifecycle, check, changed, dryRun: true, version), ct);
            return;
        }

        if (!check.Allowed)
        {
            await SendRefusalAsync(check, ct);
            return;
        }

        if (!changed)
        {
            await Send.OkAsync(Answer(def, req.Lifecycle, check, changed: false, dryRun: false, version), ct);
            return;
        }

        if (await StoreAsync(def, req.Lifecycle, ct) is not { } stored)
        {
            return;
        }

        var saved = await VersionOfAsync(stored, ct);
        await Send.OkAsync(Answer(stored, req.Lifecycle, check, changed: true, dryRun: false, saved), ct);
    }

    /// <summary>
    /// Stores the lifecycle on a fresh read of the type, or answers 409 and returns null when the
    /// type is no longer the one the counts were taken against.
    /// </summary>
    /// <remarks>
    /// The counts read the type's entries, and the definition has no concurrency check. So it is read
    /// again, compared with what was counted against, and only this member is changed on that copy.
    /// </remarks>
    private async Task<ContentTypeDefinition?> StoreAsync(
        ContentTypeDefinition counted, LifecycleDefinition proposed, CancellationToken ct)
    {
        var countedAgainst = counted.Lifecycle;
        if (AfterCountForTests is { } hook)
        {
            await hook(counted, ct);
        }

        var def = await session.LoadAsync<ContentTypeDefinition>(counted.Id, ct);
        if (def is null
            || !Same(def.Lifecycle, countedAgainst)
            || !validator.ValidateLifecycle(proposed, def.Fields).IsValid)
        {
            AddError("The content type changed while this ran. Nothing was written, so send it again.");
            await Send.ErrorsAsync(409, ct);
            return null;
        }

        var before = def.Lifecycle;
        def.Lifecycle = proposed;
        def.UpdatedAt = DateTimeOffset.UtcNow;
        session.Store(def);

        await AuditLog.RecordAsync(
            session,
            tenant.Slug,
            "contenttype.lifecycle.changed",
            ActingCaller.IdOf(User),
            User.FindFirst("Username")?.Value,
            targetType: "ContentType",
            targetId: def.Id.ToString(),
            metadata: new Dictionary<string, object>
            {
                ["contentType"] = def.Name,
                ["from"] = (object?)before ?? "none",
                ["to"] = proposed,
            },
            ct: ct);

        await session.SaveChangesAsync(ct);
        openApiCache.Invalidate(tenant.Slug);
        return def;
    }

    /// <summary>
    /// Reads <c>If-Match</c>. Says whether it answered (400 or 428) and the version the caller claims, which
    /// is null only for a dry run that sent none.
    /// </summary>
    private async Task<(bool Answered, Guid? Claimed)> ReadIfMatchAsync(Request req, CancellationToken ct)
    {
        var header = HttpContext.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrEmpty(header))
        {
            if (!req.DryRun)
            {
                AddError("If-Match is required to edit a lifecycle. GET the content type and send its version back as If-Match.");
                await Send.ErrorsAsync(428, ct);
                return (true, null);
            }

            return (false, null);
        }

        if (!ContentETag.TryParse(header, out var parsed))
        {
            AddError("If-Match is not a valid ETag.");
            await Send.ErrorsAsync(400, ct);
            return (true, null);
        }

        return (false, parsed);
    }

    /// <summary>
    /// Answers 409 while a field change runs and 412 when the claimed version is stale, and returns
    /// null then. Otherwise returns the version read under the lock.
    /// </summary>
    private async Task<Guid?> GateAsync(ContentTypeDefinition def, Guid? claimed, CancellationToken ct)
    {
        if (await FieldChanges.RunningAsync(session, def.Name, ct) is { } running)
        {
            AddError(FieldChanges.Busy(running));
            await Send.ErrorsAsync(409, ct);
            return null;
        }

        var version = await VersionOfAsync(def, ct);
        if (claimed is { } sent && sent != version)
        {
            await SendStaleAsync(def, version, ct);
            return null;
        }

        return version;
    }

    private async Task<bool> ValidAsync(LifecycleDefinition lifecycle, ContentTypeDefinition def, CancellationToken ct)
    {
        var (valid, errors) = validator.ValidateLifecycle(lifecycle, def.Fields);
        if (valid)
        {
            return true;
        }

        foreach (var error in errors)
        {
            AddError(error);
        }

        await Send.ErrorsAsync(400, ct);
        return false;
    }

    /// <summary>The type's stored document version, which is what a client reads before it edits.</summary>
    private async Task<Guid> VersionOfAsync(ContentTypeDefinition def, CancellationToken ct)
    {
        var version = (await session.MetadataForAsync(def, ct)).CurrentVersion;
        HttpContext.Response.Headers.ETag = ContentETag.Format(version);
        return version;
    }

    private async Task SendStaleAsync(ContentTypeDefinition def, Guid version, CancellationToken ct)
    {
        const string reason = "The lifecycle changed since it was loaded. Nothing was written. Reload it and send the edit again.";
        await Send.ResultAsync(TypedResults.Json(
            new StaleRefusal { Errors = [new RefusalError("ifMatch", reason)], Lifecycle = def.Lifecycle, Version = version },
            statusCode: 412));
    }

    private static Response Answer(
        ContentTypeDefinition def, LifecycleDefinition lifecycle, LifecycleCheck check, bool changed, bool dryRun, Guid version) =>
        new()
        {
            Version = version,
            Name = def.Name,
            Lifecycle = lifecycle,
            DryRun = dryRun,
            Changed = changed && !dryRun,
            CanApply = check.Allowed,
            Blockers = check.Blockers,
            Warnings = check.Warnings,
        };

    private async Task SendRefusalAsync(LifecycleCheck check, CancellationToken ct)
    {
        var errors = check.Blockers.Select(b => new RefusalError(b.Kind, b.Message)).ToList();
        await Send.ResultAsync(TypedResults.Json(
            new Refusal { Errors = errors, Blockers = check.Blockers, Warnings = check.Warnings }, statusCode: 409));
    }

    private async Task NotFoundOrAccountOwnedAsync(string name, CancellationToken ct)
    {
        if (await AccountContentTypes.FindAsync(session, name, ct) is not null)
        {
            AddError("This Content Type is owned by the account. An account manager changes it through /api/accounts/{slug}/content-types.");
            await Send.ErrorsAsync(403, ct);
            return;
        }

        await Send.NotFoundAsync(ct);
    }

    /// <summary>Exactly equal, case and order included: the comparison the reload is held to.</summary>
    private static bool Same(LifecycleDefinition? a, LifecycleDefinition? b) =>
        JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

    // Lets a test change the stored lifecycle between the counts above and the save below, which a
    // request cannot do deterministically. Null outside tests.
    internal static Func<ContentTypeDefinition, CancellationToken, Task>? AfterCountForTests { get; set; }
}

using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;

namespace barakoCMS.Features.ContentType.Create;

internal class Request
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>This type's own states, or null for Draft, Published and Archived.</summary>
    public barakoCMS.Models.LifecycleDefinition? Lifecycle { get; set; }
    public string Description { get; set; } = string.Empty;
    public List<FieldDefinition> Fields { get; set; } = new();

    /// <summary>
    /// Serve this type from the anonymous public delivery API. Defaults to false: a type is not
    /// published to the world because someone forgot to say otherwise.
    /// </summary>
    public bool IsPubliclyDeliverable { get; set; }

    /// <summary>
    /// Make the event stream the source of truth for entries of this type. Permanent.
    /// </summary>
    /// <remarks>
    /// Defaults to false, which is what every content type that exists today is: the document is the
    /// source of truth, events are still appended for history, audit, workflows and integration, and
    /// nothing rebuilds state from them. An upgrade therefore changes nothing for a deployment that
    /// does not ask for this.
    ///
    /// The choice is recorded against the NAME and cannot be changed afterwards, in either
    /// direction, including by deleting the type and creating it again.
    /// </remarks>
    public bool EventSourced { get; set; }
}

internal class Response
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>The standing sourcing decision for this name, which a recreated name inherits.</summary>
    public bool EventSourced { get; set; }
}

internal class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.Services.IContentTypeValidatorService _validator;
    private readonly barakoCMS.Infrastructure.OpenApi.DeliveryDocumentCache _openApiCache;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;
    private readonly barakoCMS.Core.Interfaces.IContentSourcingPolicy _sourcing;

    public Endpoint(
        IDocumentSession session,
        barakoCMS.Infrastructure.Services.IContentTypeValidatorService validator,
        barakoCMS.Infrastructure.OpenApi.DeliveryDocumentCache openApiCache,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant,
        barakoCMS.Core.Interfaces.IContentSourcingPolicy sourcing)
    {
        _session = session;
        _validator = validator;
        _openApiCache = openApiCache;
        _tenant = tenant;
        _sourcing = sourcing;
    }

    public override void Configure()
    {
        Post("/api/content-types");
        // SuperAdmin belongs here for the same reason it is on the other three content-type
        // routes: PermissionResolver treats the name as a blanket bypass, so a gate that excludes
        // it says the highest role may set a field's sensitivity and toggle public delivery but
        // may not create the type those settings live on. It read "Only admins can change schema"
        // from the original commit and was never revisited. See #448.
        Definition.RequireCapability(SystemCapabilities.ManageContentTypes, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        // 1. Validate ContentType
        var (isValid, errors) = _validator.Validate(req.Name, req.DisplayName, req.Fields);

        var (lifecycleValid, lifecycleErrors) = _validator.ValidateLifecycle(req.Lifecycle);
        if (!lifecycleValid)
        {
            isValid = false;
            errors.AddRange(lifecycleErrors);
        }
        if (!isValid)
        {
            // Was the one endpoint emitting two error shapes: this list, and ProblemDetails from
            // the duplicate-name ThrowError below.
            foreach (var error in errors)
            {
                AddError(error);
            }

            ThrowIfAnyErrors();
        }

        // 2. Normalize Name (slugify). Shared with the importer, which is the other way a name is
        // stored; see ContentTypeName for what went wrong when they each had their own.
        var slug = barakoCMS.Core.ContentTypeName.Normalize(req.Name);

        // 3. Check Uniqueness. This read is the friendly path, not the guarantee: the unique index on
        // the name is what actually stops two concurrent creates, and the catch below turns its
        // constraint violation into this same answer instead of a 500.
        //
        // Lowered on both sides, not compared exactly. Names are normalised on the way in from 4.0
        // and were not before, so a 3.x import could have stored "Article". Postgres compares that
        // exactly and finds nothing, while every reader in the codebase matches names with
        // OrdinalIgnoreCase and considers it the same type. That gap let "article" be created beside
        // it, and created with the opposite sourcing answer.
        var existing = await _session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(x => x.Name.ToLower() == slug, ct);

        if (existing != null)
        {
            ThrowError(DuplicateName, 409);
        }

        // 4. Sourcing policy. Read before anything is written, because two of the three answers here
        // are refusals and a refusal has to happen before the type exists.
        var standing = await _sourcing.GetAsync(slug, ct);

        if (standing is not null && standing.EventSourced != req.EventSourced)
        {
            // The delete-and-recreate hole, closed. The policy is keyed by the name and outlives the
            // definition, so a name that was decided once cannot arrive at the opposite answer by
            // being deleted and created again. Turning it on has no history to rebuild from; turning
            // it off discards the record callers rely on. Neither is recoverable.
            ThrowError(
                $"'{slug}' was created before with eventSourced set to {Lower(standing.EventSourced)}, "
                + $"on {standing.DecidedAt:yyyy-MM-dd}, and that decision belongs to the name rather "
                + "than to the type. Content and streams written under it are still here. Create this "
                + $"type with eventSourced set to {Lower(standing.EventSourced)}, or choose another name.",
                409);
        }

        if (standing is null && req.EventSourced)
        {
            // An event-sourced type has to be event sourced from its first entry. A name with
            // entries behind it has a stream written under document-mode rules, and entries older
            // than the release that completed the events carry no Sensitivity at all: a rebuild
            // would produce records that look right and are readable by roles that should not see
            // them, which is a security regression no "the document came back" assertion catches.
            // Case-insensitive for the same reason as the duplicate check above: entries created
            // before names were normalised carry whatever the caller typed, and counting none of
            // them is what let a name with history be claimed as event sourced.
            var entries = await _session.Query<barakoCMS.Models.Content>()
                .CountAsync(c => c.ContentType.ToLower() == slug, ct);

            if (entries > 0)
            {
                ThrowError(
                    $"'{slug}' already has {entries} {(entries == 1 ? "entry" : "entries")} written "
                    + "under document sourcing, so there is no history the stream can claim to be the "
                    + "source of truth for. Event sourcing has to be chosen before the first entry.",
                    409);
            }
        }

        if (req.EventSourced)
        {
            // Decision 2 of #230. An immutable stream and a legal obligation to erase are in direct
            // conflict, and the two cheap ways out are both worse: documenting the limitation gets
            // ignored, and tombstoning appends a redaction event while the payload stays in earlier
            // events, which looks like erasure and is not. Refusing the combination means personal
            // data structurally cannot enter a stream it cannot be erased from, and the operator
            // finds out here rather than during a data subject request.
            //
            // A Public field can still hold a name, so this is a mitigation and not a guarantee.
            //
            // Relaxable later without breaking anything. Not tightenable later without breaking
            // every type already created, which is why it goes in now.
            var nonPublic = req.Fields
                .Where(f => f.Sensitivity != SensitivityLevel.Public)
                .Select(f => f.Name)
                .ToList();

            if (nonPublic.Count > 0)
            {
                ThrowError(
                    $"An event-sourced type may not hold non-Public fields, and {string.Join(", ", nonPublic)} "
                    + (nonPublic.Count == 1 ? "is" : "are")
                    + " not Public. Erasing a value from an append-only stream is not something this "
                    + "server can do, so the combination is refused rather than documented. Keep the "
                    + "fields Public, or create the type with eventSourced set to false.",
                    400);
            }
        }

        // 5. Create
        var def = new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = slug,
            DisplayName = req.DisplayName,
            Lifecycle = req.Lifecycle,
            Description = req.Description,
            Fields = req.Fields,
            IsPubliclyDeliverable = req.IsPubliclyDeliverable,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _session.Store(def);

        // Written once for a name and never deleted, which is what makes recreating it inherit
        // rather than re-decide. Staged into the same transaction as the definition, so a type
        // cannot exist without its policy.
        var policy = await _sourcing.DecideAsync(slug, req.EventSourced, ct);

        try
        {
            await _session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            // The other half of the race: both requests read nothing, both inserted, and the database
            // refused the second. Same answer as the read above rather than the raw Postgres error.
            ThrowError(DuplicateName, 409);
        }

        // A new deliverable type is three new paths in the OpenAPI document, and the point is that
        // they show up without a restart.
        _openApiCache.Invalidate(_tenant.Slug);

        await Send.OkAsync(new Response
        {
            Id = def.Id,
            Name = def.Name,
            EventSourced = policy.EventSourced,
        }, ct);
    }

    private static string Lower(bool value) => value ? "true" : "false";

    private const string DuplicateName = "A Content Type with this name already exists.";

    /// <summary>
    /// Is this a Postgres unique-constraint violation (SQLSTATE 23505), at any depth?
    /// </summary>
    /// <remarks>
    /// Marten wraps the Npgsql exception, and how deeply depends on which command failed, so the
    /// chain is walked rather than the top-level type matched.
    /// </remarks>
    private static bool IsUniqueViolation(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is Npgsql.PostgresException { SqlState: "23505" })
            {
                return true;
            }
        }

        return false;
    }
}

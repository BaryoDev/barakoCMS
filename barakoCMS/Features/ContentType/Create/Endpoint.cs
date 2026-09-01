using FastEndpoints;
using Marten;
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
}

internal class Response
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

internal class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.Services.IContentTypeValidatorService _validator;
    private readonly barakoCMS.Infrastructure.OpenApi.DeliveryDocumentCache _openApiCache;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public Endpoint(
        IDocumentSession session,
        barakoCMS.Infrastructure.Services.IContentTypeValidatorService validator,
        barakoCMS.Infrastructure.OpenApi.DeliveryDocumentCache openApiCache,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _session = session;
        _validator = validator;
        _openApiCache = openApiCache;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Post("/api/content-types");
        Roles("Admin"); // Only admins can change schema
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
        var existing = await _session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(x => x.Name == slug, ct);

        if (existing != null)
        {
            ThrowError(DuplicateName, 409);
        }

        // 4. Create
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

        await Send.OkAsync(new Response { Id = def.Id, Name = def.Name }, ct);
    }

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

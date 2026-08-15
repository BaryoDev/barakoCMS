using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.ContentType.Create;

public class Request
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<FieldDefinition> Fields { get; set; } = new();

    /// <summary>
    /// Serve this type from the anonymous public delivery API. Defaults to false: a type is not
    /// published to the world because someone forgot to say otherwise.
    /// </summary>
    public bool IsPubliclyDeliverable { get; set; }
}

public class Response
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<string>? Errors { get; set; }
}

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.Services.IContentTypeValidatorService _validator;

    public Endpoint(IDocumentSession session, barakoCMS.Infrastructure.Services.IContentTypeValidatorService validator)
    {
        _session = session;
        _validator = validator;
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
        if (!isValid)
        {
            await SendAsync(new Response { Errors = errors }, 400, ct);
            return;
        }

        // 2. Normalize Name (slugify)
        var slug = req.Name.ToLowerInvariant().Trim().Replace(" ", "-");

        // 3. Check Uniqueness
        var existing = await _session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(x => x.Name == slug, ct);

        if (existing != null)
        {
            ThrowError("A Content Type with this name already exists.");
        }

        // 4. Create
        var def = new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = slug,
            DisplayName = req.DisplayName,
            Description = req.Description,
            Fields = req.Fields,
            IsPubliclyDeliverable = req.IsPubliclyDeliverable,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _session.Store(def);
        await _session.SaveChangesAsync(ct);

        await SendOkAsync(new Response { Id = def.Id, Name = def.Name }, ct);
    }
}

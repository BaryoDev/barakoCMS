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
}

public class Response
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Post("/api/content-types");
        Roles("Admin"); // Only admins can change schema
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        // 1. Normalize Name (slugify)
        var slug = req.Name.ToLowerInvariant().Trim().Replace(" ", "-");

        // 2. Check Uniqueness
        var existing = await _session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(x => x.Name == slug, ct);

        if (existing != null)
        {
            ThrowError("A Content Type with this name already exists.");
        }

        // 3. Create
        var def = new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = slug,
            DisplayName = req.DisplayName,
            Description = req.Description,
            Fields = req.Fields,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _session.Store(def);
        await _session.SaveChangesAsync(ct);

        await SendOkAsync(new Response { Id = def.Id, Name = def.Name }, ct);
    }
}

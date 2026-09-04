using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;

namespace barakoCMS.Features.ContentType.Blueprints.List;

internal sealed class Response
{
    public List<Item> Items { get; init; } = new();

    /// <summary>Problems with the custom directory itself, as opposed to one file in it.</summary>
    public List<string> Problems { get; init; } = new();
}

internal sealed class Item
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool BuiltIn { get; init; }
    public string? Source { get; init; }
    public List<string> ContentTypes { get; init; } = new();

    /// <summary>Empty for a blueprint that can be applied. Anything here and the apply refuses it.</summary>
    public List<string> Errors { get; init; } = new();
}

/// <summary>GET /api/content-types/blueprints. The blueprints this instance can apply.</summary>
internal sealed class Endpoint : EndpointWithoutRequest<Response>
{
    private readonly BlueprintCatalog _catalog;

    public Endpoint(BlueprintCatalog catalog)
    {
        _catalog = catalog;
    }

    public override void Configure()
    {
        Get("/api/content-types/blueprints");
        // Reading what a blueprint would create is reading schema, the same grant as listing types.
        Definition.RequireCapability(SystemCapabilities.ManageContentTypes, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var entries = _catalog.All(out var problems);

        await Send.OkAsync(new Response
        {
            Items = entries.Select(e => new Item
            {
                Name = e.Name,
                Description = e.Description,
                BuiltIn = e.BuiltIn,
                Source = e.Source,
                ContentTypes = e.ContentTypes.ToList(),
                Errors = e.Errors.ToList(),
            }).ToList(),
            Problems = problems.ToList(),
        }, ct);
    }
}

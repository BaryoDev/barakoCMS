using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Forms.Features.Definition;

internal sealed class OptionResponse
{
    public string Value { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

internal sealed class FieldResponse
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool Required { get; set; }
    public Dictionary<string, object> ValidationRules { get; set; } = new();
    /// <summary>For a choice field, its options in display order. Empty for any other type.</summary>
    public List<OptionResponse> Options { get; set; } = new();
    /// <summary>For a choice field, whether it takes a list of options rather than one.</summary>
    public bool Multiple { get; set; }
}

internal sealed class Response
{
    public string Slug { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<FieldResponse> Fields { get; set; } = new();
}

/// <summary>
/// GET /api/public/forms/{slug}. Anonymous. The fields a widget draws, which are exactly the fields
/// the submit endpoint accepts: nothing non-Public, nothing of a type a form cannot render.
/// </summary>
internal sealed class Endpoint(IQuerySession session) : EndpointWithoutRequest<Response>
{
    public override void Configure()
    {
        Get("/api/public/forms/{slug}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var slug = Route<string>("slug") ?? string.Empty;

        var form = await session.LoadAsync<PublicForm>(slug, ct);
        var definition = form is null
            ? null
            : await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == slug, ct);

        if (definition is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(new Response
        {
            Slug = definition.Name,
            DisplayName = definition.DisplayName,
            Description = definition.Description,
            Fields = FormFields.Submittable(definition).Select(f => new FieldResponse
            {
                Name = f.Name,
                DisplayName = f.DisplayName,
                Type = f.Type,
                Required = f.IsRequired,
                ValidationRules = f.ValidationRules,
                Options = (f.Options ?? new List<FieldOption>())
                    .Select(o => new OptionResponse { Value = o.Value, Label = o.Label })
                    .ToList(),
                Multiple = f.Multiple,
            }).ToList(),
        }, ct);
    }
}

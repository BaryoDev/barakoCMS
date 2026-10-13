using System.Text.Json;
using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.Config.Plan;

internal sealed class Request
{
    /// <summary>A configuration document, as <c>GET /api/config/export</c> returns one.</summary>
    public JsonElement Document { get; set; }
}

internal sealed class Validator : Validator<Request>
{
    public Validator()
    {
        RuleFor(r => r.Document.ValueKind).Equal(JsonValueKind.Object)
            .WithName("document")
            .WithMessage("document must be a configuration document, a JSON object.");
    }
}

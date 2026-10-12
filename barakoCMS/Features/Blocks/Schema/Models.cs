using System.Text.Json;
using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.Blocks.Schema;

internal sealed class Request
{
    /// <summary>Each block type by name, with the JSON Schema of its properties.</summary>
    public Dictionary<string, JsonElement>? Blocks { get; set; }
}

internal sealed class Response
{
    public Dictionary<string, JsonElement> Blocks { get; set; } = new();

    /// <summary><c>renderer</c> when the renderer key registered it, else the id of the user who did.</summary>
    public string RegisteredBy { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }

    public static Response From(barakoCMS.Models.BlockSchema schema) => new()
    {
        Blocks = schema.Blocks,
        RegisteredBy = schema.RegisteredBy,
        UpdatedAt = schema.UpdatedAt,
    };
}

/// <summary>The renderer's request: the same body, judged only once its key has matched.</summary>
internal sealed class RendererRequest
{
    public Dictionary<string, JsonElement>? Blocks { get; set; }
}

internal sealed class Validator : Validator<Request>
{
    public Validator()
    {
        RuleFor(r => r.Blocks).Custom((blocks, context) =>
        {
            foreach (var error in SchemaRules.Errors(blocks))
            {
                context.AddFailure("blocks", error);
            }
        });
    }
}

internal static class SchemaRules
{
    /// <summary>The most a whole schema may be, as JSON text.</summary>
    public const int MaxLength = 256 * 1024;

    /// <summary>The most a request body to either PUT may be, which bounds what is read before anything is judged.</summary>
    public const long MaxBodyBytes = 512 * 1024;

    public static IEnumerable<string> Errors(Dictionary<string, JsonElement>? blocks) =>
        blocks is not null && JsonSerializer.Serialize(blocks).Length > MaxLength
            ? [$"A block schema is at most {MaxLength / 1024} KiB."]
            : barakoCMS.Core.Validation.BlockFields.SchemaErrors(blocks);
}

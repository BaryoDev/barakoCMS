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

internal sealed class Validator : Validator<Request>
{
    /// <summary>The most a whole schema may be, as JSON text.</summary>
    public const int MaxLength = 256 * 1024;

    public Validator()
    {
        RuleFor(r => r.Blocks).Custom((blocks, context) =>
        {
            if (blocks is not null && JsonSerializer.Serialize(blocks).Length > MaxLength)
            {
                context.AddFailure("blocks", $"A block schema is at most {MaxLength / 1024} KiB.");
                return;
            }

            foreach (var error in barakoCMS.Core.Validation.BlockFields.SchemaErrors(blocks))
                context.AddFailure("blocks", error);
        });
    }
}

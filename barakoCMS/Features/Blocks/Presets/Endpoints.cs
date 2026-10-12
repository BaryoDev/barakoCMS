using System.Text.Json;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation;
using Marten;

namespace barakoCMS.Features.Blocks.Presets;

internal sealed class PresetResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string BlockType { get; set; } = string.Empty;
    public JsonElement Props { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static PresetResponse From(BlockPreset p) => new()
    {
        Id = p.Id, Name = p.Name, BlockType = p.BlockType, Props = p.Props, UpdatedAt = p.UpdatedAt,
    };
}

/// <summary>GET /api/blocks/presets: the tenant's saved blocks, a page at a time, by name.</summary>
internal sealed class ListEndpoint(IQuerySession session) : Endpoint<PaginatedRequest, PaginatedResponse<PresetResponse>>
{
    public override void Configure()
    {
        Get("/api/blocks/presets");
        Description(b => b.WithTags("Blocks"));
    }

    public override async Task HandleAsync(PaginatedRequest req, CancellationToken ct)
    {
        var query = session.Query<BlockPreset>().OrderBy(p => p.Name).ThenBy(p => p.Id);
        var page = await query.Skip(req.Skip).Take(req.Take).ToListAsync(ct);

        await Send.OkAsync(new PaginatedResponse<PresetResponse>
        {
            Items = page.Select(PresetResponse.From).ToList(),
            Page = req.Page,
            PageSize = req.PageSize,
            TotalItems = await query.CountAsync(ct),
        }, ct);
    }
}

internal sealed class PutRequest
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string BlockType { get; set; } = string.Empty;
    public JsonElement Props { get; set; }
}

internal sealed class PutValidator : Validator<PutRequest>
{
    public const int MaxNameLength = 100;
    public const int MaxPropsLength = 64 * 1024;

    public PutValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(MaxNameLength);
        RuleFor(r => r.BlockType).NotEmpty().MaximumLength(64);
        RuleFor(r => r.Props).Must(p => p.ValueKind == JsonValueKind.Object)
            .WithMessage("A preset's props are an object of the block's properties.");
        RuleFor(r => r.Props).Must(p => p.ValueKind != JsonValueKind.Object || p.GetRawText().Length <= MaxPropsLength)
            .WithMessage($"A preset's props are at most {MaxPropsLength / 1024} KiB.");
    }
}

/// <summary>PUT /api/blocks/presets/{id}: creates or replaces a saved block.</summary>
/// <remarks>
/// The props are checked against the block's schema when the tenant has one, as a block on a page
/// is, so a preset cannot hold what a page would refuse. A tenant holds at most
/// <see cref="MaxPresets"/>.
/// </remarks>
internal sealed class PutEndpoint(IDocumentSession session) : Endpoint<PutRequest, PresetResponse>
{
    public const int MaxPresets = 500;

    public override void Configure()
    {
        Put("/api/blocks/presets/{id}");
        Definition.RequireCapability(SystemCapabilities.ManageContentTypes, "SuperAdmin", "Admin");
        Description(b => b.WithTags("Blocks"));
    }

    public override async Task HandleAsync(PutRequest req, CancellationToken ct)
    {
        foreach (var error in await BlockFields.PropsErrorsAsync(session, req.BlockType, req.Props, "props", ct))
        {
            AddError(error);
        }

        ThrowIfAnyErrors();

        var existing = await session.LoadAsync<BlockPreset>(req.Id, ct);
        if (existing is null && await session.Query<BlockPreset>().CountAsync(ct) >= MaxPresets)
        {
            AddError($"A tenant holds at most {MaxPresets} presets. Delete one to save another.");
            ThrowIfAnyErrors(StatusCodes.Status409Conflict);
        }

        var preset = new BlockPreset
        {
            Id = req.Id, Name = req.Name.Trim(), BlockType = req.BlockType, Props = req.Props, UpdatedAt = DateTimeOffset.UtcNow,
        };
        session.Store(preset);
        await session.SaveChangesAsync(ct);
        await Send.OkAsync(PresetResponse.From(preset), ct);
    }
}

internal sealed class DeleteRequest
{
    public Guid Id { get; set; }
}

/// <summary>DELETE /api/blocks/presets/{id}: removes a saved block. A page built from it keeps its own copy.</summary>
internal sealed class DeleteEndpoint(IDocumentSession session) : Endpoint<DeleteRequest>
{
    public override void Configure()
    {
        Delete("/api/blocks/presets/{id}");
        Definition.RequireCapability(SystemCapabilities.ManageContentTypes, "SuperAdmin", "Admin");
        Description(b => b.WithTags("Blocks"));
    }

    public override async Task HandleAsync(DeleteRequest req, CancellationToken ct)
    {
        if (await session.LoadAsync<BlockPreset>(req.Id, ct) is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        session.Delete<BlockPreset>(req.Id);
        await session.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}

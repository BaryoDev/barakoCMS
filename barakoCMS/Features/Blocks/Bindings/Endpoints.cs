using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation;
using Marten;

namespace barakoCMS.Features.Blocks.Bindings;

internal sealed class BindingResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string? Sort { get; set; }
    public int Limit { get; set; }
    public Dictionary<string, string>? Where { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static BindingResponse From(BlockBinding b) => new()
    {
        Id = b.Id, Name = b.Name, ContentType = b.ContentType, Sort = b.Sort, Limit = b.Limit, Where = b.Where,
        UpdatedAt = b.UpdatedAt,
    };
}

/// <summary>GET /api/blocks/bindings: the tenant's data bindings, a page at a time, by name.</summary>
internal sealed class ListEndpoint(IQuerySession session) : Endpoint<PaginatedRequest, PaginatedResponse<BindingResponse>>
{
    public override void Configure()
    {
        Get("/api/blocks/bindings");
        Description(b => b.WithTags("Blocks"));
    }

    public override async Task HandleAsync(PaginatedRequest req, CancellationToken ct)
    {
        var query = session.Query<BlockBinding>().OrderBy(b => b.Name).ThenBy(b => b.Id);
        var page = await query.Skip(req.Skip).Take(req.Take).ToListAsync(ct);

        await Send.OkAsync(new PaginatedResponse<BindingResponse>
        {
            Items = page.Select(BindingResponse.From).ToList(),
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
    public string ContentType { get; set; } = string.Empty;
    public string? Sort { get; set; }
    public int Limit { get; set; } = 3;
    public Dictionary<string, string>? Where { get; set; }
}

internal sealed class PutValidator : Validator<PutRequest>
{
    public const int MaxWhere = 5;

    public PutValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(100);
        RuleFor(r => r.ContentType).NotEmpty().MaximumLength(100);
        RuleFor(r => r.Limit).InclusiveBetween(1, BlockBinding.MaxLimit);
        RuleFor(r => r.Sort).MaximumLength(101);
        RuleFor(r => r.Where).Must(w => w is null || (w.Count <= MaxWhere && w.Values.All(v => v is { Length: <= 200 })))
            .WithMessage($"At most {MaxWhere} conditions, each value at most 200 characters.");
    }
}

/// <summary>PUT /api/blocks/bindings/{id}: creates or replaces a data binding.</summary>
/// <remarks>
/// The type has to exist, and the sort and every condition have to name its fields, so a binding
/// cannot be saved that page resolve would have to skip. Whether the type is publicly deliverable,
/// and whether those fields are Public, is read again on every resolve, since either can change
/// after the binding is saved. A tenant holds at most <see cref="MaxBindings"/>.
///
/// Refused with 409 while a field change runs on the type: a rename moves the fields bindings name
/// in one of its steps, and a binding saved around that step would keep the old name.
/// </remarks>
internal sealed class PutEndpoint(IDocumentSession session) : Endpoint<PutRequest, BindingResponse>
{
    public const int MaxBindings = 200;

    public override void Configure()
    {
        Put("/api/blocks/bindings/{id}");
        Definition.RequireCapability(SystemCapabilities.ManageContentTypes, "SuperAdmin", "Admin");
        Description(b => b.WithTags("Blocks"));
    }

    public override async Task HandleAsync(PutRequest req, CancellationToken ct)
    {
        // Taken before the type is read, so a field change that ran in between is not missed.
        await FieldChanges.LockTypeAsync(session, req.ContentType.Trim(), ct);
        var lowered = req.ContentType.Trim().ToLower();
        var definition = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name.ToLower() == lowered, ct);
        if (definition is not null && await FieldChanges.RunningAsync(session, definition.Name, ct) is { } running)
        {
            AddError(FieldChanges.Busy(running));
            ThrowIfAnyErrors(StatusCodes.Status409Conflict);
        }

        if (definition is null)
        {
            AddError(r => r.ContentType, $"There is no content type '{req.ContentType}'.");
        }
        else
        {
            foreach (var field in Fields(req).Where(f => !definition.Fields.Any(d => string.Equals(d.Name, f, StringComparison.OrdinalIgnoreCase))))
            {
                AddError($"'{definition.Name}' has no field '{field}'.");
            }
        }

        ThrowIfAnyErrors();

        if (await session.LoadAsync<BlockBinding>(req.Id, ct) is null && await session.Query<BlockBinding>().CountAsync(ct) >= MaxBindings)
        {
            AddError($"A tenant holds at most {MaxBindings} bindings. Delete one to save another.");
            ThrowIfAnyErrors(StatusCodes.Status409Conflict);
        }

        var binding = new BlockBinding
        {
            Id = req.Id, Name = req.Name.Trim(), ContentType = definition!.Name, Sort = string.IsNullOrWhiteSpace(req.Sort) ? null : req.Sort.Trim(),
            Limit = req.Limit, Where = req.Where is { Count: > 0 } ? req.Where : null, UpdatedAt = DateTimeOffset.UtcNow,
        };
        session.Store(binding);
        await session.SaveChangesAsync(ct);
        await Send.OkAsync(BindingResponse.From(binding), ct);
    }

    private static IEnumerable<string> Fields(PutRequest req) =>
        (req.Where?.Keys ?? Enumerable.Empty<string>())
            .Concat(string.IsNullOrWhiteSpace(req.Sort) ? [] : [req.Sort.Trim().TrimStart('-')]);
}

internal sealed class DeleteRequest
{
    public Guid Id { get; set; }
}

/// <summary>DELETE /api/blocks/bindings/{id}: removes a binding. A block naming it then resolves no entries.</summary>
internal sealed class DeleteEndpoint(IDocumentSession session) : Endpoint<DeleteRequest>
{
    public override void Configure()
    {
        Delete("/api/blocks/bindings/{id}");
        Definition.RequireCapability(SystemCapabilities.ManageContentTypes, "SuperAdmin", "Admin");
        Description(b => b.WithTags("Blocks"));
    }

    public override async Task HandleAsync(DeleteRequest req, CancellationToken ct)
    {
        if (await session.LoadAsync<BlockBinding>(req.Id, ct) is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        session.Delete<BlockBinding>(req.Id);
        await session.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}

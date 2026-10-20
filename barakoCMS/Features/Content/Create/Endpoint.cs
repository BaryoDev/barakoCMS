using barakoCMS.Core.Interfaces;
using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using System.Security.Claims;

namespace barakoCMS.Features.Content.Create;

internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver,
    barakoCMS.Infrastructure.Services.IContentCreator creator,
    barakoCMS.Infrastructure.Services.IContentBatchRunner runner) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/api/contents");
        Claims("UserId");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var (userId, user) = await CallerAsync(ct);

        if (!await permissionResolver.CanPerformActionAsync(user, req.ContentType, "create", null, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        // Sensitivity, validation and lifecycle hooks, then the write. The same path an import runs,
        // so what one route refuses the other refuses too.
        var request = new barakoCMS.Infrastructure.Services.ContentCreateRequest
        {
            ContentType = req.ContentType,
            Data = req.Data,
            Status = req.Status,
            Sensitivity = req.Sensitivity,
        };

        if (req.Lines is { Count: > 0 })
        {
            await CreateWithLinesAsync(req, request, userId, user, ct);
            return;
        }

        var errors = await creator.CheckAsync(request, userId, HttpContext, batch: null, ct);
        if (errors.Count > 0)
        {
            // One entry per failure rather than one flattened string, so a client can show the
            // failures against the fields they belong to.
            foreach (var error in errors)
            {
                AddError(error);
            }

            ThrowIfAnyErrors();
        }

        // The check above asks only whether a Create rule exists. This one asks whether a rule
        // holds for the entry as it will be stored, so its conditions apply to a create as well.
        if (!await permissionResolver.AllowsCreatedEntryAsync(user, request, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        if (req.DryRun)
        {
            await Send.ResponseAsync(new Response { DryRun = true, Changes = barakoCMS.Core.Validation.DryRuns.Between(null, request.Data) });
            return;
        }

        var created = await creator.StageAsync(request, userId, batch: null, ct);

        await session.SaveChangesAsync(ct);

        // Workflows are triggered out-of-band by the async WorkflowProjection reacting to the
        // committed ContentCreated event — deliberately NOT awaited here, so a slow or failing
        // workflow action can never block or fail the content save.

        await Send.ResponseAsync(new Response
        {
            Id = created.Id,
            Version = 1,
        });
    }

    /// <summary>
    /// A header and its lines, written in one transaction. Nothing is stored unless every entry
    /// passes, and a refused line names its position.
    /// </summary>
    private async Task CreateWithLinesAsync(
        Request req, barakoCMS.Infrastructure.Services.ContentCreateRequest request, Guid userId, User user, CancellationToken ct)
    {
        var name = barakoCMS.Core.ContentTypeName.Normalize(req.ContentType);
        var lineType = (await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name.ToLower() == name, ct))?.Lines;
        if (lineType is null)
        {
            AddError($"Content type '{req.ContentType}' does not take lines.");
            ThrowIfAnyErrors();
            return;
        }

        var lineDefinition = await session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name.ToLower() == barakoCMS.Core.ContentTypeName.Normalize(lineType.ContentType), ct);
        var parent = lineDefinition?.Fields.FirstOrDefault(f => string.Equals(f.Name, lineType.ParentField, StringComparison.OrdinalIgnoreCase));
        if (parent is null
            || !string.Equals(parent.Type, "reference", StringComparison.OrdinalIgnoreCase)
            || parent.Multiple
            || !string.Equals(parent.ReferenceType, name, StringComparison.OrdinalIgnoreCase))
        {
            AddError($"The lines of '{req.ContentType}' no longer point at it: '{lineType.ContentType}' has no single reference field '{lineType.ParentField}' to it. Set the lines again.");
            ThrowIfAnyErrors();
            return;
        }

        var headerId = Guid.NewGuid();
        var header = new barakoCMS.Infrastructure.Services.ContentCreateRequest
        {
            ContentType = request.ContentType, Data = request.Data, Status = request.Status,
            Sensitivity = request.Sensitivity, Id = headerId,
        };
        var lines = req.Lines!.Select(l => LineWriter.Line(lineType, l, headerId, req.Status, req.Sensitivity)).ToList();

        var allowed = await permissionResolver.CanPerformActionAsync(user, lineType.ContentType, "create", null, ct)
            && await permissionResolver.AllowsCreatedEntryAsync(user, header, ct);
        foreach (var line in lines)
        {
            allowed = allowed && await permissionResolver.AllowsCreatedEntryAsync(user, line, ct);
        }

        if (!allowed)
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var result = await LineWriter.WriteAsync(runner, header, lines, userId, req.DryRun, HttpContext, ct);
        foreach (var error in result.Errors)
        {
            AddError(error);
        }

        ThrowIfAnyErrors();

        await Send.ResponseAsync(req.DryRun
            ? new Response { DryRun = true, Changes = barakoCMS.Core.Validation.DryRuns.Between(null, header.Data) }
            : new Response { Id = headerId, Version = 1, LineIds = result.LineIds }, cancellation: ct);
    }

    /// <summary>The signed-in user the write is made for.</summary>
    private async Task<(Guid Id, User User)> CallerAsync(CancellationToken ct)
    {
        var userIdClaim = User.FindFirst("UserId");
        if (userIdClaim == null)
        {
            ThrowError("User ID claim not found");
        }

        if (!Guid.TryParse(userIdClaim.Value, out var userId))
        {
            ThrowError("Invalid User ID format");
        }

        var user = await session.LoadAsync<User>(userId, ct);
        if (user == null)
        {
            ThrowError("User not found", 401);
        }

        return (userId, user);
    }
}

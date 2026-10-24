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
            // The create rule is asked of the data the caller may write before any save rule runs,
            // and again below of the entry as it will be stored. A field a rule sets only when it
            // passes would otherwise turn the second answer into a report on the rule.
            Permitted = (entry, token) => permissionResolver.AllowsCreatedEntryAsync(user, entry, token),
        };

        if (req.Lines is { Count: > 0 } || await DeclaresTotalAsync(req.ContentType, ct))
        {
            await CreateWithLinesAsync(req, request, userId, user, ct);
            return;
        }

        if (await CheckOrRefuseAsync(request, userId, ct) is not { } errors) return;

        if (errors.Count == 0)
        {
            errors = await barakoCMS.Features.Content.LineTotals.CheckCreateAsync(session, request.ContentType, request.Data, ct);
        }

        if (request.Forbidden || (errors.Count > 0 && !await RefuseAsync(user, request, errors, ct)))
        {
            await Send.ForbiddenAsync(ct);
            return;
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

        barakoCMS.Models.Content created;
        try
        {
            created = await creator.StageAsync(request, userId, batch: null, ct);
        }
        catch (barakoCMS.Infrastructure.Services.ContentCreateRefusedException refused)
        {
            AddError(new FluentValidation.Results.ValidationFailure("numbering", refused.Message));
            ThrowIfAnyErrors();
            return;
        }

        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (JasperFx.DocumentAlreadyExistsException e) when (e.DocType == typeof(barakoCMS.Models.ExternalNumberUse))
        {
            // Two creates took the same client number at once; the ledger let the first one have it.
            AddError(new FluentValidation.Results.ValidationFailure("numbering", "That number is already issued."));
            ThrowIfAnyErrors(409);
            return;
        }

        // Workflows are triggered out-of-band by the async WorkflowProjection reacting to the
        // committed ContentCreated event — deliberately NOT awaited here, so a slow or failing
        // workflow action can never block or fail the content save.

        await Send.ResponseAsync(new Response
        {
            Id = created.Id,
            Version = 1,
        });
    }

    /// <summary>The checks' failures, or null after answering 400 because the type name is ambiguous.</summary>
    private async Task<IReadOnlyList<string>?> CheckOrRefuseAsync(
        barakoCMS.Infrastructure.Services.ContentCreateRequest request, Guid userId, CancellationToken ct)
    {
        try
        {
            return await creator.CheckAsync(request, userId, HttpContext, batch: null, ct);
        }
        catch (barakoCMS.Core.AmbiguousContentTypeException ambiguous)
        {
            AddError(new FluentValidation.Results.ValidationFailure("contentType", ambiguous.Message));
            ThrowIfAnyErrors();
            return null;
        }
    }

    /// <summary>
    /// Answers the create's failures: a rule refusal as 422, anything else as 400, one entry per
    /// failure so a client can show them against their fields. A rule may read other entries, and
    /// whether the checks after it ran says whether it passed, so any refusal goes only to a caller
    /// whose create rule holds for this entry; false means it does not, and the caller gets 403.
    /// </summary>
    private async Task<bool> RefuseAsync(User user, barakoCMS.Infrastructure.Services.ContentCreateRequest request,
        IReadOnlyList<string> errors, CancellationToken ct)
    {
        if (!await permissionResolver.AllowsCreatedEntryAsync(user, request, ct))
        {
            return false;
        }

        if (request.RuleFailure is { } refused)
        {
            AddError(new FluentValidation.Results.ValidationFailure("rules", refused));
            ThrowIfAnyErrors(422);
        }

        foreach (var error in errors)
        {
            AddError(error);
        }

        ThrowIfAnyErrors();
        return true;
    }

    private async Task<bool> DeclaresTotalAsync(string contentType, CancellationToken ct)
    {
        var name = barakoCMS.Core.ContentTypeName.Normalize(contentType);
        return (await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name.ToLower() == name, ct))?.Lines?.TotalField is not null;
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
            Sensitivity = request.Sensitivity, Id = headerId, Permitted = request.Permitted,
        };
        var lines = (req.Lines ?? []).Select(l => LineWriter.Line(lineType, l, headerId, req.Status, req.Sensitivity)).ToList();

        if (lines.Count > 0 && !await permissionResolver.CanPerformActionAsync(user, lineType.ContentType, "create", null, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var result = await LineWriter.WriteAsync(
            runner, header, lines, userId, req.DryRun, HttpContext,
            entry => permissionResolver.AllowsCreatedEntryAsync(user, entry, ct), lineType, ct);
        if (result.Forbidden)
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        foreach (var error in result.Errors)
        {
            AddError(error);
        }

        ThrowIfAnyErrors(result.RuleRefused ? 422 : 400);

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

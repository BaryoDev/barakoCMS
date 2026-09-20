using FastEndpoints;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Workflows;

internal class CreateWorkflowEndpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Services.IWorkflowSchemaValidator validator,
    ISecretProtector protector) : Endpoint<WorkflowDefinition, barakoCMS.Features.Workflows.WorkflowResponse>
{
    public override void Configure()
    {
        Post("/api/workflows");
        Definition.RequireCapability(SystemCapabilities.ManageWorkflows, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(WorkflowDefinition req, CancellationToken ct)
    {
        // Validate before persisting so invalid trigger events / unknown action types / missing
        // required parameters are rejected up front rather than silently never firing (or firing twice).
        var validation = await validator.ValidateAsync(req, ct);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
            {
                AddError($"{error.Field}: {error.Message}");
            }
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        // Stored as the content type declares it. The engine matches this with an equality query, so
        // a workflow saved as "transition:approve" against a transition named "Approve" would be
        // accepted here and then never fire.
        if (validation.NormalisedTriggerEvent is { Length: > 0 } declared)
        {
            req.TriggerEvent = declared;
        }

        WorkflowTriggers.Normalise(req);

        // Encrypted before it is stored, so the definition, the runs that copy its parameters and
        // the execution log all hold ciphertext. Only the webhook action decrypts it, when sending.
        WebhookSigning.ProtectSecrets(req, protector);

        req.Id = Guid.NewGuid();
        session.Store(req);
        await session.SaveChangesAsync(ct);
        await Send.ResponseAsync(barakoCMS.Features.Workflows.WorkflowResponse.From(req), cancellation: ct);
    }
}

internal class ListWorkflowsEndpoint(
    IDocumentSession session) : Endpoint<ListRequest, PaginatedResponse<barakoCMS.Features.Workflows.WorkflowResponse>>
{
    public override void Configure()
    {
        Get("/api/workflows");
        Definition.RequireCapability(SystemCapabilities.ManageWorkflows, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var page = await session.Query<WorkflowDefinition>()
            .OrderBy(w => w.Name)
            .ToPagedResponseAsync(req, ct);

        await Send.ResponseAsync(new PaginatedResponse<barakoCMS.Features.Workflows.WorkflowResponse>
        {
            Items = page.Items.Select(barakoCMS.Features.Workflows.WorkflowResponse.From).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, cancellation: ct);
    }
}

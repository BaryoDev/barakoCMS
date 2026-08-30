using FastEndpoints;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Workflows;

internal class CreateWorkflowEndpoint : Endpoint<WorkflowDefinition, barakoCMS.Features.Workflows.WorkflowResponse>
{
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.Services.IWorkflowSchemaValidator _validator;

    public CreateWorkflowEndpoint(IDocumentSession session, barakoCMS.Infrastructure.Services.IWorkflowSchemaValidator validator)
    {
        _session = session;
        _validator = validator;
    }

    public override void Configure()
    {
        Post("/api/workflows");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(WorkflowDefinition req, CancellationToken ct)
    {
        // Validate before persisting so invalid trigger events / unknown action types / missing
        // required parameters are rejected up front rather than silently never firing (or firing twice).
        var validation = _validator.Validate(req, ct);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
            {
                AddError($"{error.Field}: {error.Message}");
            }
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        req.Id = Guid.NewGuid();
        _session.Store(req);
        await _session.SaveChangesAsync(ct);
        await Send.ResponseAsync(barakoCMS.Features.Workflows.WorkflowResponse.From(req), cancellation: ct);
    }
}

internal class ListWorkflowsEndpoint : Endpoint<ListRequest, PaginatedResponse<barakoCMS.Features.Workflows.WorkflowResponse>>
{
    private readonly IDocumentSession _session;

    public ListWorkflowsEndpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Get("/api/workflows");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var page = await _session.Query<WorkflowDefinition>()
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

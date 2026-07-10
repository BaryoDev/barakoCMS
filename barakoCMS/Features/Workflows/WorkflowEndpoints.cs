using FastEndpoints;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Workflows;

public class CreateWorkflowEndpoint : Endpoint<WorkflowDefinition, WorkflowDefinition>
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
            await SendErrorsAsync(cancellation: ct);
            return;
        }

        req.Id = Guid.NewGuid();
        _session.Store(req);
        await _session.SaveChangesAsync(ct);
        await SendAsync(req, cancellation: ct);
    }
}

public class ListWorkflowsEndpoint : EndpointWithoutRequest<List<WorkflowDefinition>>
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

    public override async Task HandleAsync(CancellationToken ct)
    {
        var workflows = await _session.Query<WorkflowDefinition>().ToListAsync(ct);
        await SendAsync(workflows.ToList(), cancellation: ct);
    }
}

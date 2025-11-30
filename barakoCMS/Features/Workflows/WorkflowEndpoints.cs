using FastEndpoints;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Workflows;

public class CreateWorkflowEndpoint : Endpoint<WorkflowDefinition, WorkflowDefinition>
{
    private readonly IDocumentSession _session;

    public CreateWorkflowEndpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Post("/api/workflows");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(WorkflowDefinition req, CancellationToken ct)
    {
        Console.WriteLine($"[SERVER] User Claims: {string.Join(", ", User.Claims.Select(c => $"{c.Type}={c.Value}"))}");
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

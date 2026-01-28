using barakoCMS.Features.WorkflowsV2.Actions;
using barakoCMS.Features.WorkflowsV2.Services;
using FastEndpoints;

namespace barakoCMS.Features.WorkflowsV2.Endpoints;

public class ActionInfo
{
    public string Type { get; set; } = "";
    public string Category { get; set; } = "";
    public string Description { get; set; } = "";
    public ActionConfigSchema? ConfigSchema { get; set; }
}

public class ListActionsResponse
{
    public List<ActionInfo> Actions { get; set; } = new();
    public Dictionary<string, List<string>> Categories { get; set; } = new();
}

public class ListActionsEndpoint : EndpointWithoutRequest<ListActionsResponse>
{
    private readonly IActionRegistry _actionRegistry;

    public ListActionsEndpoint(IActionRegistry actionRegistry)
    {
        _actionRegistry = actionRegistry;
    }

    public override void Configure()
    {
        Get("/api/workflows/v2/actions");
        Roles("Admin", "WorkflowAdmin", "WorkflowViewer");
        Description(b => b.WithTags("WorkflowsV2"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var actions = _actionRegistry.GetAllActions();

        var actionInfos = actions.Select(a => new ActionInfo
        {
            Type = a.Type,
            Category = a.Category,
            Description = a.Description,
            ConfigSchema = a.GetConfigSchema()
        }).ToList();

        var categories = actionInfos
            .GroupBy(a => a.Category)
            .ToDictionary(
                g => g.Key,
                g => g.Select(a => a.Type).ToList()
            );

        await SendAsync(new ListActionsResponse
        {
            Actions = actionInfos,
            Categories = categories
        }, cancellation: ct);
    }
}

// Get specific action schema
public class GetActionSchemaRequest
{
    public string Type { get; set; } = "";
}

public class GetActionSchemaEndpoint : Endpoint<GetActionSchemaRequest, ActionInfo>
{
    private readonly IActionRegistry _actionRegistry;

    public GetActionSchemaEndpoint(IActionRegistry actionRegistry)
    {
        _actionRegistry = actionRegistry;
    }

    public override void Configure()
    {
        Get("/api/workflows/v2/actions/{Type}");
        Roles("Admin", "WorkflowAdmin", "WorkflowViewer");
        Description(b => b.WithTags("WorkflowsV2"));
    }

    public override async Task HandleAsync(GetActionSchemaRequest req, CancellationToken ct)
    {
        var action = _actionRegistry.GetAction(req.Type);

        if (action == null)
        {
            await SendNotFoundAsync(ct);
            return;
        }

        await SendAsync(new ActionInfo
        {
            Type = action.Type,
            Category = action.Category,
            Description = action.Description,
            ConfigSchema = action.GetConfigSchema()
        }, cancellation: ct);
    }
}

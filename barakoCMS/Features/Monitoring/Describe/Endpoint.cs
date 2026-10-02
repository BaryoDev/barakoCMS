using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Modules;
using FastEndpoints;

namespace barakoCMS.Features.Monitoring.Describe;

/// <summary>
/// GET /api/meta/describe, what this instance accepts: field types and the rules each takes, and
/// for a caller who may read them the capabilities, workflow actions and running modules.
/// </summary>
/// <remarks>
/// Signed-in callers only, like <c>GET /api/meta</c> beside it, and not role-restricted: anyone
/// editing a content type needs the field types. The three other parts repeat what
/// <c>GET /api/capabilities</c>, <c>GET /api/workflows/actions</c> and <c>GET /api/modules</c>
/// already return, so each is left out for a caller those endpoints would refuse.
///
/// Every list is read from memory. The only database read is the capability lookup for the three
/// gated parts, at most three per request, answered by the permission cache after the first.
/// </remarks>
internal sealed class Endpoint(
    CapabilityVocabulary vocabulary,
    ModuleCatalogue catalogue) : EndpointWithoutRequest<DescribeResponse>
{
    public override void Configure()
    {
        Get("/api/meta/describe");
        Description(b => b
            .Produces<DescribeResponse>(200)
            .Produces(401)
            .WithTags("Monitoring"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        // The body differs by what the caller holds, and a grant can be taken away, so no cache
        // keeps a copy to hand to the next caller or to the same one after a revocation.
        HttpContext.Response.Headers.CacheControl = "no-store";

        var document = new DescribeResponse
        {
            ApiContractVersion = barakoCMS.Features.Monitoring.Meta.ApiContract.Version,
            FieldTypes = DescribeDocument.FieldTypes(FieldTypeRegistry.Types, FieldRules.Names),
            Rules = DescribeDocument.Rules(FieldRules.Names),
        };

        if (await CapabilityGateProcessor.HoldsAsync(HttpContext, DescribeDocument.CapabilitiesGate, ct))
        {
            document.Capabilities = vocabulary.Entries;
        }

        if (await CapabilityGateProcessor.HoldsAsync(HttpContext, DescribeDocument.WorkflowActionsGate, ct))
        {
            // Resolved here and not injected: building the registry constructs every registered
            // action, which a caller who is not shown them should not cost.
            var registry = HttpContext.RequestServices.GetRequiredService<IWorkflowPluginRegistry>();
            document.WorkflowActions = DescribeDocument.WorkflowActions(registry.GetAllActions());
        }

        if (await CapabilityGateProcessor.HoldsAsync(HttpContext, DescribeDocument.ModulesGate, ct))
        {
            document.Modules = DescribeDocument.Modules(catalogue);
        }

        await Send.OkAsync(document, ct);
    }
}

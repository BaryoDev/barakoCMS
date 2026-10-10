using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Modules;
using FastEndpoints;

namespace barakoCMS.Features.Monitoring.Describe;

/// <summary>
/// GET /api/meta/describe, what this instance accepts: field types and the rules each takes, the
/// editor hints and roles a field may declare, and for a caller who may read them the
/// capabilities, workflow actions and running modules.
/// </summary>
/// <remarks>
/// Signed-in callers only, like <c>GET /api/meta</c> beside it, and not role-restricted: anyone
/// editing a content type needs the field types. The three other parts repeat what
/// <c>GET /api/capabilities</c>, <c>GET /api/workflows/actions</c> and <c>GET /api/modules</c>
/// already return, so each is left out for a caller those endpoints would refuse.
///
/// Every list is read from memory. The database is read only to answer the three capability
/// checks: each is a user load, a membership lookup and a role query the first time, and is then
/// answered by the permission cache, which keys on the capability. So a cold request is three
/// checks of up to three queries each, and a warm one is none.
///
/// The response is not kept by a cache, whatever its status: the route is one of
/// <see cref="barakoCMS.Infrastructure.Security.SecurityHeaders.IsNoStorePath"/>'s.
/// </remarks>
internal sealed class Endpoint(
    CapabilityVocabulary vocabulary,
    ModuleCatalogue catalogue,
    ILogger<Endpoint> logger) : EndpointWithoutRequest<DescribeResponse>
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
        var document = new DescribeResponse
        {
            ApiContractVersion = barakoCMS.Features.Monitoring.Meta.ApiContract.Version,
            FieldTypes = DescribeDocument.FieldTypes(FieldTypeRegistry.Types, FieldRules.Names),
            Rules = DescribeDocument.Rules(FieldRules.Names),
            FieldEditors = DescribeDocument.FieldHints(FieldPresentation.Editors),
            FieldRoles = DescribeDocument.FieldHints(FieldPresentation.Roles),
            StructuredDataTypes = barakoCMS.Core.Validation.StructuredDataTypes.Names,
            CredentialNameParts = barakoCMS.Infrastructure.Security.CredentialNames.Words,
        };

        if (await CapabilityGateProcessor.HoldsAsync(HttpContext, DescribeDocument.CapabilitiesGate, ct))
        {
            document.Capabilities = vocabulary.Entries;
        }

        if (await CapabilityGateProcessor.HoldsAsync(HttpContext, DescribeDocument.WorkflowActionsGate, ct))
        {
            document.WorkflowActions = ReadWorkflowActions();
        }

        if (await CapabilityGateProcessor.HoldsAsync(HttpContext, DescribeDocument.ModulesGate, ct))
        {
            document.Modules = DescribeDocument.Modules(catalogue);
        }

        await Send.OkAsync(document, ct);
    }

    /// <summary>
    /// The registered actions, or null when the registry cannot be built.
    /// </summary>
    /// <remarks>
    /// Resolved here and not injected. Building the registry constructs every registered action, so
    /// a caller who is not shown the actions does not pay for it, and one module action whose
    /// constructor throws costs this part of the document and not the field types beside it.
    ///
    /// Only the exception's type is logged. Its message is whatever a module's constructor chose to
    /// put there, which can be a setting or a connection detail.
    /// </remarks>
    private IReadOnlyList<barakoCMS.Models.WorkflowActionMetadata>? ReadWorkflowActions()
    {
        try
        {
            var registry = HttpContext.RequestServices.GetRequiredService<IWorkflowPluginRegistry>();
            return DescribeDocument.WorkflowActions(registry.GetAllActions());
        }
        catch (Exception ex)
        {
            logger.LogError(
                "The describe document left out workflowActions because the action registry could not be read: {ExceptionType}",
                ex.GetType().Name);
            return null;
        }
    }
}

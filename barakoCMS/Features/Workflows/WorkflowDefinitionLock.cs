using Marten;

namespace barakoCMS.Features.Workflows;

/// <summary>Makes a delete of a workflow and a retry of one of its runs take turns.</summary>
/// <remarks>
/// A retry checks that the workflow still exists and then queues an attempt. A delete cancels the
/// queued runs and then removes the workflow. Run side by side, the retry could pass its check
/// before the delete committed and queue its attempt after the delete had looked for queued runs,
/// leaving an attempt with no definition, which the runner executes.
///
/// A lock and not a version check on the definition, because the retry writes nothing on the
/// definition and the definition carries no version. Held to the end of the session's transaction,
/// so it is released when the request saves or gives up, and neither request makes an outbound call
/// while it holds it. The key names the tenant, since the same id in two tenants is two workflows.
/// </remarks>
internal static class WorkflowDefinitionLock
{
    internal static async Task TakeAsync(IDocumentSession session, Guid workflowId, CancellationToken ct)
    {
        await session.BeginTransactionAsync(ct);
        await session.QueryAsync<int>(
            "select 1 from pg_advisory_xact_lock(hashtextextended(?, 0))", ct, Key(session.TenantId, workflowId));
    }

    internal static string Key(string tenantId, Guid workflowId) =>
        $"barakocms:workflow-definition:{tenantId}:{workflowId:N}";
}

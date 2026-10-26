using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Realtime;

/// <summary>
/// The hub topic that says a workflow run was written. Runs are documents, not events, so this topic
/// has no feed behind it: a notice means "read <c>GET /api/workflow-runs?changedSince=</c>".
/// </summary>
internal static class WorkflowRunTopic
{
    public const string Name = "workflow-runs";

    public const string Capability = SystemCapabilities.ViewWorkflowRuns;

    /// <summary>More runs than this in one commit are announced with a single refetch notice.</summary>
    public const int MaxNoticesPerCommit = 50;

    public const string RefetchType = "WorkflowRunsChanged";

    /// <summary>
    /// The legacy role names the list endpoint accepts when <c>Auth:LegacyRoleFallback</c> is on.
    /// The hub applies the same rule so a subscribe and a read answer alike.
    /// </summary>
    public static bool LegacyRoleAllows(Microsoft.Extensions.Configuration.IConfiguration config, System.Security.Claims.ClaimsPrincipal user) =>
        config.GetValue(barakoCMS.Infrastructure.Auth.CapabilityGateProcessor.LegacyRoleFallbackKey, false)
        && barakoCMS.Features.WorkflowRuns.RunGate.LegacyRoles.Any(user.IsInRole);

    /// <summary>One notice for a commit that wrote many runs: read the list again. It names no run.</summary>
    public static RealtimeNotice RefetchNotice() => new(Name, 0, Guid.Empty, RefetchType);

    /// <summary>The notice for one run. It has no event sequence, so the sequence is zero.</summary>
    public static RealtimeNotice Notice(Guid runId) => new(Name, 0, runId, nameof(WorkflowRun));
}

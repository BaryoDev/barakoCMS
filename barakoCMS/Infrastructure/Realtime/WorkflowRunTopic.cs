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

    /// <summary>The notice for one run. It has no event sequence, so the sequence is zero.</summary>
    public static RealtimeNotice Notice(Guid runId) => new(Name, 0, runId, nameof(WorkflowRun));
}

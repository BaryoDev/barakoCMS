using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.WorkflowRuns;

/// <summary>
/// Reads over many runs at once that one grouped statement answers, where a query per status or per
/// workflow would not.
/// </summary>
/// <remarks>
/// Raw SQL because Marten's LINQ has no grouped count over a JSON field. Marten adds no tenant
/// filter to SQL it is handed, so every statement here names the session's tenant. Ids are embedded
/// as literals only after being parsed as <see cref="Guid"/>; nothing a caller typed is embedded.
/// </remarks>
internal static class RunQueries
{
    private const string Created = "public.mt_immutable_timestamptz(data ->> 'CreatedAt')";

    internal static string Table(IQuerySession session) =>
        $"{session.DocumentStore.Options.DatabaseSchemaName}.mt_doc_workflow_runs";

    internal static string InList(IEnumerable<Guid> ids) =>
        string.Join(", ", ids.Select(id => $"'{id:D}'"));

    /// <summary>The run count of every status, zero where there is none, in one statement.</summary>
    internal static async Task<Dictionary<string, int>> CountByStatusAsync(
        IQuerySession session, Guid? contentId, Guid? workflowId, CancellationToken ct)
    {
        var where = "tenant_id = ?";
        if (contentId is { } c) where += $" and data ->> 'ContentId' = '{c:D}'";
        if (workflowId is { } w) where += $" and data ->> 'WorkflowDefinitionId' = '{w:D}'";

        var rows = await session.QueryAsync<string>(
            $"select (data ->> 'Status') || ':' || count(*) from {Table(session)} where {where} group by data ->> 'Status'",
            ct, session.TenantId);

        var counts = Enum.GetValues<RunStatus>().ToDictionary(s => s.ToString(), _ => 0);
        foreach (var row in rows)
        {
            var parts = row.Split(':');
            counts[((RunStatus)int.Parse(parts[0])).ToString()] = int.Parse(parts[1]);
        }

        return counts;
    }

    /// <summary>Per workflow and status, the runs created at or after a time, in one statement.</summary>
    internal static async Task<Dictionary<(Guid Workflow, RunStatus Status), int>> CountSinceAsync(
        IQuerySession session, IReadOnlyCollection<Guid> workflowIds, DateTimeOffset from, CancellationToken ct)
    {
        var result = new Dictionary<(Guid, RunStatus), int>();
        if (workflowIds.Count == 0) return result;

        var rows = await session.QueryAsync<string>(
            $"select (data ->> 'WorkflowDefinitionId') || ':' || (data ->> 'Status') || ':' || count(*) from {Table(session)} "
          + $"where tenant_id = ? and data ->> 'WorkflowDefinitionId' in ({InList(workflowIds)}) and {Created} >= ? "
          + "group by data ->> 'WorkflowDefinitionId', data ->> 'Status'",
            ct, session.TenantId, from.UtcDateTime);

        foreach (var row in rows)
        {
            var parts = row.Split(':');
            result[(Guid.Parse(parts[0]), (RunStatus)int.Parse(parts[1]))] = int.Parse(parts[2]);
        }

        return result;
    }

    /// <summary>
    /// Per workflow, the mean recorded duration of the actions of its newest runs created at or after
    /// a time. An action with no duration is left out, and a workflow with none has no entry.
    /// </summary>
    internal static async Task<Dictionary<Guid, double>> AverageDurationAsync(
        IQuerySession session, IReadOnlyCollection<Guid> workflowIds, DateTimeOffset from, int sample, CancellationToken ct)
    {
        var result = new Dictionary<Guid, double>();
        if (workflowIds.Count == 0) return result;

        var rows = await session.QueryAsync<string>(
            "select s.wf || ':' || avg((a ->> 'DurationMs')::numeric)::text from ("
          + "select data ->> 'WorkflowDefinitionId' as wf, "
          + "case when jsonb_typeof(data -> 'Actions') = 'array' then data -> 'Actions' else '[]'::jsonb end as acts, "
          + $"row_number() over (partition by data ->> 'WorkflowDefinitionId' order by {Created} desc) as rn "
          + $"from {Table(session)} where tenant_id = ? and data ->> 'WorkflowDefinitionId' in ({InList(workflowIds)}) and {Created} >= ?"
          + ") s cross join lateral jsonb_array_elements(s.acts) a "
          + $"where s.rn <= {sample} and (a ->> 'DurationMs') is not null group by s.wf",
            ct, session.TenantId, from.UtcDateTime);

        foreach (var row in rows)
        {
            var at = row.IndexOf(':');
            result[Guid.Parse(row[..at])] = double.Parse(row[(at + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        }

        return result;
    }

    /// <summary>Per workflow, its newest finished runs (not pending, not running), newest first, in one statement.</summary>
    internal static async Task<ILookup<Guid, WorkflowRun>> RecentFinishedAsync(
        IQuerySession session, IReadOnlyCollection<Guid> workflowIds, int perWorkflow, CancellationToken ct)
    {
        if (workflowIds.Count == 0) return Array.Empty<WorkflowRun>().ToLookup(r => r.WorkflowDefinitionId);

        var runs = await session.QueryAsync<WorkflowRun>(
            "select data from ("
          + $"select data, row_number() over (partition by data ->> 'WorkflowDefinitionId' order by {Created} desc) as rn "
          + $"from {Table(session)} where tenant_id = ? and data ->> 'WorkflowDefinitionId' in ({InList(workflowIds)}) "
          + $"and (data ->> 'Status')::int not in ({(int)RunStatus.Pending}, {(int)RunStatus.Running})"
          + $") s where rn <= {perWorkflow} order by rn",
            ct, session.TenantId);

        return runs.ToLookup(r => r.WorkflowDefinitionId);
    }
}

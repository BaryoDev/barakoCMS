namespace barakoCMS.Modules;

/// <summary>
/// The needs-you items <c>GET /api/overview</c> returns, readable by a module for a tenant and a set of
/// capabilities, without a request.
/// </summary>
/// <remarks>
/// <para>
/// The items are the route's: the same ids, the same text, the same order, the same rules. An item is
/// left out when the set lacks the capability the route asks for it:
/// <c>view_workflow_runs</c> for failing workflows, <c>view_monitoring</c> for the runner,
/// <c>manage_email_settings</c> for bounces (and a registered bounce source). Overdue publishes need
/// content read, which a capability set cannot express, so they are included only for a set that holds
/// <c>*</c>. A set that holds less never sees them from here, even a user with a per-type read
/// rule or a branch scope, whom the route does show the overdue entries they may read. This is deliberate.
/// A capability string cannot carry those rules, so the source grants less than the route, never more.
/// </para>
/// <para>
/// Get the set from <see cref="barakoCMS.Core.Interfaces.ICapabilityResolver"/>. A module must resolve
/// the set and not write one, because a wider set shows more.
/// </para>
/// <para>
/// Every read is a count or a capped list for the named tenant. Nothing is written, so it is safe to
/// poll.
/// </para>
/// </remarks>
public interface INeedsYouSource
{
    /// <summary>The items for <paramref name="tenant"/>, worst first and then by id, as the route orders them.</summary>
    Task<IReadOnlyList<NeedsYouEntry>> ReadAsync(
        string tenant, IReadOnlySet<string> capabilities, CancellationToken cancellationToken = default);
}

/// <summary>One thing that needs a person.</summary>
/// <param name="Id">Stable across reads while the cause lasts. One of <c>mail.bounces</c>, <c>workflows.failing</c>, <c>content.overdue</c>, <c>runner.stalled</c>.</param>
/// <param name="Severity"><c>warn</c> or <c>bad</c>.</param>
/// <param name="Text">A short line a person reads.</param>
/// <param name="RefKind">Where to fix it: <c>mailBounces</c>, <c>workflowRuns</c>, <c>content</c> or <c>runner</c>.</param>
/// <param name="RefId">An id inside that place, or null.</param>
/// <param name="Resolvable">Whether <c>POST /api/overview/items/{id}/resolve</c> clears it.</param>
public sealed record NeedsYouEntry(
    string Id, string Severity, string Text, string RefKind, string? RefId, bool Resolvable);

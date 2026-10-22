using System.Security.Claims;
using barakoCMS.Infrastructure.Envelope;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Whether a caller may see or touch an entry of a branch scoped type: only when the entry has a
/// branch and the caller holds it. A type that is not branch scoped is never restricted.
/// </summary>
/// <remarks>
/// One rule for the change feed and the content routes. The caller's branches and the set of
/// branch scoped types are read once per request.
/// </remarks>
internal interface IBranchScopeGuard
{
    /// <summary>True when the stored type of that name is branch scoped.</summary>
    Task<bool> IsScopedAsync(string contentType, CancellationToken ct);

    /// <summary>Whether the caller may reach an entry of the type with that branch.</summary>
    Task<bool> MayAccessAsync(ClaimsPrincipal caller, string contentType, Guid? entryBranch, CancellationToken ct);

    /// <summary>The same, when the caller already knows whether the type is branch scoped.</summary>
    Task<bool> MayAccessAsync(ClaimsPrincipal caller, bool branchScoped, Guid? entryBranch, CancellationToken ct);

    /// <summary>Whether the caller may reach the stored entry.</summary>
    Task<bool> MayAccessAsync(ClaimsPrincipal caller, Content entry, CancellationToken ct);

    /// <summary>
    /// A SQL condition over the content table (alias <c>d</c>) that keeps the rows the caller may
    /// reach, with its parameters, or null when no stored type is branch scoped.
    /// </summary>
    Task<BranchScopeFilter?> FilterAsync(ClaimsPrincipal caller, CancellationToken ct);
}

/// <summary>A condition for <c>MatchesSql</c> and the values of its two <c>?</c> placeholders.</summary>
internal sealed record BranchScopeFilter(string Sql, object[] Parameters);

internal sealed class BranchScopeGuard(IQuerySession session, IBranchMembership membership) : IBranchScopeGuard
{
    private HashSet<string>? _scoped;
    private ClaimsPrincipal? _heldFor;
    private HashSet<Guid>? _held;

    public async Task<bool> IsScopedAsync(string contentType, CancellationToken ct) =>
        (await ScopedAsync(ct)).Contains(contentType.ToLowerInvariant());

    public async Task<bool> MayAccessAsync(
        ClaimsPrincipal caller, string contentType, Guid? entryBranch, CancellationToken ct) =>
        await MayAccessAsync(caller, await IsScopedAsync(contentType, ct), entryBranch, ct);

    public async Task<bool> MayAccessAsync(
        ClaimsPrincipal caller, bool branchScoped, Guid? entryBranch, CancellationToken ct)
    {
        if (!branchScoped) return true;
        return entryBranch is { } branch && (await HeldAsync(caller, ct)).Contains(branch);
    }

    public Task<bool> MayAccessAsync(ClaimsPrincipal caller, Content entry, CancellationToken ct) =>
        MayAccessAsync(caller, entry.ContentType, entry.BranchId, ct);

    public async Task<BranchScopeFilter?> FilterAsync(ClaimsPrincipal caller, CancellationToken ct)
    {
        var scoped = await ScopedAsync(ct);
        if (scoped.Count == 0) return null;

        var held = (await HeldAsync(caller, ct)).Select(b => b.ToString()).ToArray();
        return new BranchScopeFilter(
            "(lower(d.data ->> 'ContentType') <> ALL(?) OR (d.data ->> 'BranchId') = ANY(?))",
            [scoped.ToArray(), held]);
    }

    private async Task<HashSet<string>> ScopedAsync(CancellationToken ct)
    {
        if (_scoped is not null) return _scoped;

        var names = await session.Query<ContentTypeDefinition>()
            .Where(d => d.BranchScoped)
            .Select(d => d.Name)
            .ToListAsync(ct);
        return _scoped = names.Select(n => n.ToLowerInvariant()).ToHashSet();
    }

    private async Task<HashSet<Guid>> HeldAsync(ClaimsPrincipal caller, CancellationToken ct)
    {
        if (_held is not null && ReferenceEquals(_heldFor, caller)) return _held;

        _held = (await membership.BranchesOfAsync(caller, ct)).ToHashSet();
        _heldFor = caller;
        return _held;
    }
}

using barakoCMS.Features.Roles;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <summary>Finds role permissions that name a transition the edit removes or changes.</summary>
internal sealed class LifecycleRoleScanner(barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : ILifecycleScanner
{
    public async Task<LifecycleScan> ScanAsync(IQuerySession session, LifecycleChange change, CancellationToken ct)
    {
        var blockers = new List<LifecycleBlocker>();
        if (change.ChangedTransitions.Count == 0)
        {
            return LifecycleScan.None;
        }

        var roles = await RoleScope.In(session.Query<Role>(), tenant.Slug).ToListAsync(ct);
        foreach (var role in roles.OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            var permissions = (role.Permissions ?? []).Where(p => p is not null
                && barakoCMS.Core.ContentTypeName.Same(p.ContentTypeSlug, change.Type.Name));
            foreach (var name in permissions.SelectMany(p => (p.Transitions ?? []).Keys).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (LifecycleReferences.Changes(change, name) is { } changed)
                {
                    blockers.Add(new LifecycleBlocker(
                        LifecycleBlockerKind.Role, null, changed.Name, role.Name, null,
                        $"The role '{role.Name}' grants the '{changed.Name}' transition, which this edit removes or changes. "
                        + "Take it off the role first."));
                }
            }
        }

        return LifecycleScan.Blocking(blockers);
    }
}

/// <summary>Finds rules that name a state or a transition the edit removes or changes, on this type and on others.</summary>
internal sealed class LifecycleRuleScanner : ILifecycleScanner
{
    public async Task<LifecycleScan> ScanAsync(IQuerySession session, LifecycleChange change, CancellationToken ct)
    {
        if (change.From is null)
        {
            return LifecycleScan.None;
        }

        var blockers = new List<LifecycleBlocker>();
        var types = await session.Query<ContentTypeDefinition>().ToListAsync(ct);
        foreach (var owner in types.Where(t => t.Rules is { Count: > 0 }).OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var own = barakoCMS.Core.ContentTypeName.Same(owner.Name, change.Type.Name);
            foreach (var rule in owner.Rules!)
            {
                var found = new Dictionary<string, LifecycleBlocker>(StringComparer.OrdinalIgnoreCase);
                if (own && LifecycleReferences.Strands(change, rule.InState))
                {
                    found["s:" + rule.InState] = StateBlocker(owner, rule, rule.InState!);
                }

                if (own && LifecycleReferences.Changes(change, rule.Transition) is { } changed)
                {
                    found["t:" + changed.Name] = new LifecycleBlocker(
                        LifecycleBlockerKind.Rule, null, changed.Name, rule.Name, null,
                        $"The rule '{rule.Name}' runs on the '{changed.Name}' transition, which this edit removes or changes.");
                }

                foreach (var (state, _) in Conditions(owner, rule).Where(c => barakoCMS.Core.ContentTypeName.Same(c.Type, change.Type.Name))
                             .SelectMany(c => c.States.Select(s => (s, c))))
                {
                    if (LifecycleReferences.Strands(change, state))
                    {
                        found["s:" + state] = StateBlocker(owner, rule, state);
                    }
                }

                blockers.AddRange(found.Values);
            }
        }

        return LifecycleScan.Blocking(blockers);
    }

    private static LifecycleBlocker StateBlocker(ContentTypeDefinition owner, RuleDefinition rule, string state) =>
        new(LifecycleBlockerKind.Rule, state, null, rule.Name, null,
            $"The rule '{rule.Name}' on '{owner.Name}' names the state '{state}', which this edit removes or renames.");

    /// <summary>One state list a rule reads, and the type whose lifecycle it is a list of.</summary>
    private readonly record struct Reading(string? Type, IReadOnlyList<string> States);

    private static IEnumerable<Reading> Conditions(ContentTypeDefinition owner, RuleDefinition rule) =>
        Walk(owner, (rule.If ?? []).Concat(rule.Let ?? []).Concat(rule.All));

    private static IEnumerable<Reading> Walk(ContentTypeDefinition owner, IEnumerable<RuleCondition> conditions)
    {
        foreach (var c in conditions)
        {
            var states = LifecycleReferences.Listed(c.State).ToList();
            if (string.Equals(c.Kind, "state", StringComparison.OrdinalIgnoreCase))
            {
                states.AddRange(LifecycleReferences.Listed(c.Value));
            }

            // A Ref names an entry of unknown type unless the condition says which, and then no state is attributed.
            var type = c.ContentType ?? (c.Ref is null ? owner.Name : null);
            if (states.Count > 0)
            {
                yield return new Reading(type, states);
            }

            foreach (var inner in Walk(owner, (c.Each ?? []).Concat(c.If ?? [])))
            {
                yield return inner;
            }
        }
    }
}

/// <summary>Finds uniqueness rules that apply in a state the edit removes or renames.</summary>
internal sealed class LifecycleUniquenessScanner : ILifecycleScanner
{
    public Task<LifecycleScan> ScanAsync(IQuerySession session, LifecycleChange change, CancellationToken ct)
    {
        var blockers = (change.Type.Uniqueness ?? [])
            .Where(rule => LifecycleReferences.Strands(change, rule.WhenState))
            .Select(rule => new LifecycleBlocker(
                LifecycleBlockerKind.Uniqueness, rule.WhenState, null, rule.Name, null,
                $"The uniqueness rule '{rule.Name}' applies in the state '{rule.WhenState}', which this edit removes or renames. "
                + "It would stop being enforced."))
            .ToList();
        return Task.FromResult(LifecycleScan.Blocking(blockers));
    }
}

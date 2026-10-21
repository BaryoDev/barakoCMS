using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Rules;

/// <summary>A refusal, or the fields the rules wrote.</summary>
internal readonly record struct RuleOutcome(ContentTransitionResult? Failure, Dictionary<string, object> Derived)
{
    /// <summary>
    /// The derived fields laid over the move's data, under the key the entry already stores each
    /// one as. A move that sent nothing gets a copy of the stored entry to carry them.
    /// </summary>
    public Dictionary<string, object>? Over(Dictionary<string, object>? data, Dictionary<string, object> stored)
    {
        if (Derived.Count == 0) return data;
        data ??= new Dictionary<string, object>(stored, stored.Comparer);
        foreach (var (key, value) in Derived)
            data[data.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) ?? key] = value;
        return data;
    }
}

/// <summary>Runs a type's transition rules against the entry as the move would leave it.</summary>
internal static class TransitionRules
{
    /// <summary>
    /// The rules see the sent values laid over the stored entry, so a move that sends nothing is
    /// still checked against the entry's fields. Only keys a rule wrote or changed come back.
    /// </summary>
    public static async Task<RuleOutcome> CheckAsync(
        IRuleEngine rules, IDocumentSession session, Content current, string transition,
        string fromState, string toState, Guid userId, Dictionary<string, object>? sent, CancellationToken ct)
    {
        var working = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in current.Data) working[key] = value;
        foreach (var (key, value) in sent ?? []) working[key] = value;
        var before = new Dictionary<string, object?>(working, StringComparer.OrdinalIgnoreCase);

        var result = await rules.EvaluateAsync(RuleWhen.Transition,
            new RuleContext(current.ContentType, current.Id, working, current, transition, fromState, toState, userId, session), ct);
        if (!result.Passed)
            return new(ContentTransitionResult.RuleFailed(result.Message ?? "A rule failed."), []);

        var derived = working
            .Where(p => p.Value is not null && (!before.TryGetValue(p.Key, out var old) || !Equals(old, p.Value)))
            .ToDictionary(p => p.Key, p => p.Value!, StringComparer.OrdinalIgnoreCase);
        return new(null, derived);
    }
}

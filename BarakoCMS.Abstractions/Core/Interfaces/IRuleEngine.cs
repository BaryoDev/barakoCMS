using barakoCMS.Models;
using Marten;

namespace barakoCMS.Core.Interfaces;

/// <summary>What a rule sees: the entry being written, the one stored, and the transition if any.</summary>
public sealed record RuleContext(
    string ContentType, Guid? EntryId, IDictionary<string, object?> Data, Content? Existing,
    string? Transition, string? FromState, string? ToState, Guid UserId, IDocumentSession Session);

/// <summary>Whether the rules passed, and when not, the first refusal's text and rule name.</summary>
public sealed record RuleResult(bool Passed, string? Message, string? RuleName)
{
    /// <summary>Every rule passed.</summary>
    public static RuleResult Pass { get; } = new(true, null, null);
}

/// <summary>Runs a content type's rules. It may write derived fields into the data and never saves.</summary>
public interface IRuleEngine
{
    /// <summary>Runs the rules of <paramref name="when"/> against <paramref name="context"/>.</summary>
    Task<RuleResult> EvaluateAsync(RuleWhen when, RuleContext context, CancellationToken ct);
}

/// <summary>What a lookup answered: whether it found something, and its text and number.</summary>
public sealed record RuleLookupValue(bool Found, string? Text, decimal? Number);

/// <summary>A named lookup rules can call, for values the rule language cannot read on its own.</summary>
public interface IRuleLookup
{
    /// <summary>The name rules call it by.</summary>
    string Name { get; }

    /// <summary>Answers the lookup for <paramref name="args"/>.</summary>
    Task<RuleLookupValue> ResolveAsync(IReadOnlyDictionary<string, string> args, IDocumentSession session, CancellationToken ct);
}

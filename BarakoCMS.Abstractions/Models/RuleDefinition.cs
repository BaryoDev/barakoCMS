namespace barakoCMS.Models;

/// <summary>When a content type rule runs.</summary>
public enum RuleWhen
{
    /// <summary>On every create and update.</summary>
    Save,

    /// <summary>On a lifecycle transition.</summary>
    Transition,
}

/// <summary>A server check a content type declares: conditions that must hold, and fields it sets.</summary>
public sealed class RuleDefinition
{
    /// <summary>The rule's name, reported with a refusal.</summary>
    public string Name { get; set; } = "";

    /// <summary>When the rule runs.</summary>
    public RuleWhen When { get; set; } = RuleWhen.Save;

    /// <summary>The IANA zone for $NOW and $TODAY. Null uses the site's TimeZone, else UTC.</summary>
    public string? TimeZone { get; set; }

    /// <summary>The transition the rule runs on, or null for every transition.</summary>
    public string? Transition { get; set; }

    /// <summary>The state the entry must be in for the rule to run, or null for any state.</summary>
    public string? InState { get; set; }

    /// <summary>Conditions that must all hold for the rule to run. Otherwise the rule is skipped.</summary>
    public List<RuleCondition>? If { get; set; }

    /// <summary>Named values worked out in order after <see cref="If"/> and before <see cref="All"/>, read as <c>let.&lt;as&gt;</c>.</summary>
    public List<RuleCondition>? Let { get; set; }

    /// <summary>Conditions that must all hold, or the write is refused with <see cref="Message"/>.</summary>
    public List<RuleCondition> All { get; set; } = [];

    /// <summary>Fields written into the entry when the rule passes, by template.</summary>
    public Dictionary<string, string> Set { get; set; } = [];

    /// <summary>The text a refusal carries.</summary>
    public string Message { get; set; } = "";
}

/// <summary>One condition of a rule.</summary>
public sealed class RuleCondition
{
    /// <summary>field, length, pattern, each, itemCount, state, changed, unique, related, sum, count, lookup, stored, member, covers, distinct or notRequester.</summary>
    public string Kind { get; set; } = "field";

    /// <summary>The field the condition reads.</summary>
    public string? Field { get; set; }

    /// <summary>eq, ne, gt, gte, lt, lte, in, empty or notEmpty.</summary>
    public string Op { get; set; } = "eq";

    /// <summary>The value compared against, which may hold templates.</summary>
    public string? Value { get; set; }

    /// <summary>How values are normalised before comparing: trim, lower, digits.</summary>
    public List<string>? Normalise { get; set; }

    /// <summary>The other content type a related, sum, count or unique condition reads.</summary>
    public string? ContentType { get; set; }

    /// <summary>A filter on the other type's entries, as <c>Field=value;Field2={{x}}</c>.</summary>
    public string? Where { get; set; }

    /// <summary>The lifecycle states, as a comma list, a related, sum, count or member condition reads.</summary>
    public string? State { get; set; }

    /// <summary>The path a sum condition adds up, such as <c>Lines.Amount</c>.</summary>
    public string? Sum { get; set; }

    /// <summary>The name of the lookup a lookup condition calls.</summary>
    public string? Lookup { get; set; }

    /// <summary>The arguments passed to the lookup, which may hold templates.</summary>
    public Dictionary<string, string>? Args { get; set; }

    /// <summary>For kind each: conditions run against every item of the array field named by <see cref="Field"/>.</summary>
    public List<RuleCondition>? Each { get; set; }

    /// <summary>Whether the condition's result is inverted.</summary>
    public bool Negate { get; set; }

    /// <summary>Guards: the condition is checked only when all of them hold, and counts as met otherwise.</summary>
    public List<RuleCondition>? If { get; set; }

    /// <summary>A template that replaces the rule's message when this condition is the one that fails.</summary>
    public string? Message { get; set; }

    /// <summary>A template giving one entry id; field, state, related, sum, member and a field let then read that entry.</summary>
    public string? Ref { get; set; }

    /// <summary>For member and covers: a path, or a template per row such as <c>lab:{{TestCode}}</c>, giving the values.</summary>
    public string? Of { get; set; }

    /// <summary>For member: extra allowed values; for covers: extra required values. Comma separated.</summary>
    public string? Also { get; set; }

    /// <summary>For a let: the name it is read by, as <c>{{let.&lt;as&gt;}}</c> or the path <c>let.&lt;as&gt;</c>.</summary>
    public string? As { get; set; }
}

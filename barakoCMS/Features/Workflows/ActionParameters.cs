using barakoCMS.Infrastructure.Services;

namespace barakoCMS.Features.Workflows;

/// <summary>
/// Resolves an action's {{...}} parameters, encoding each value for where that parameter ends up.
/// </summary>
/// <remarks>
/// The engine, the runner, the dry run and a conditional's children all go through here, so an
/// email's body is escaped the same way whichever path sent it.
/// </remarks>
internal static class ActionParameters
{
    /// <summary>
    /// Every email provider sends the body as HTML, so a value there is HTML-encoded. The subject and
    /// recipient are headers, so a value there loses its line breaks and is otherwise left alone.
    /// </summary>
    public static TemplateValueEncoding EncodingFor(string actionType, string parameter) =>
        actionType == "Email" && parameter.Equals("Body", StringComparison.OrdinalIgnoreCase) ? TemplateValueEncoding.Html
      : actionType == "Email" && (parameter.Equals("Subject", StringComparison.OrdinalIgnoreCase)
                                  || parameter.Equals("To", StringComparison.OrdinalIgnoreCase)) ? TemplateValueEncoding.SingleLine
      : TemplateValueEncoding.None;

    /// <summary>
    /// A conditional's branches are JSON holding child actions. Resolving them as one string would
    /// let a quote in a value rewrite that JSON, and would give the child's parameters no encoding
    /// at all, so they are left as written and the conditional resolves each child's parameters.
    /// Its condition is left as written too: the conditional reads the token's value from the entry
    /// itself, and a value substituted first would be parsed as part of the comparison.
    /// </summary>
    /// <remarks>
    /// An email's attachments are left as written as well. The action reads the field its
    /// placeholder names from the entry itself, so a field holding a list of files is read as a
    /// list and not as the text a list renders to.
    /// </remarks>
    public static bool IsResolvedByTheAction(string actionType, string parameter) =>
        (actionType == "Conditional"
         && (parameter.Equals("Condition", StringComparison.OrdinalIgnoreCase)
             || parameter.Equals("ThenActions", StringComparison.OrdinalIgnoreCase)
             || parameter.Equals("ElseActions", StringComparison.OrdinalIgnoreCase)))
        || (actionType == "Email"
            && parameter.Equals(Actions.EmailAction.AttachmentsParameter, StringComparison.OrdinalIgnoreCase));

    /// <summary>The parameter the runner and the engine use to tell an action which trigger fired.</summary>
    public const string TriggerEventParameter = "TriggerEvent";

    /// <summary>
    /// Hands the parent's trigger down to a child action's parameters.
    /// </summary>
    /// <remarks>
    /// The name is reserved: a child that declares its own gets the parent's instead, the same way
    /// the runner overwrites one declared on a top-level action. A child cannot claim an event that
    /// did not fire.
    /// </remarks>
    public static Dictionary<string, string> WithTriggerOf(
        IReadOnlyDictionary<string, string> parent, Dictionary<string, string> child)
    {
        if (parent.TryGetValue(TriggerEventParameter, out var trigger))
        {
            child[TriggerEventParameter] = trigger;
        }

        return child;
    }

    public static Dictionary<string, string> Resolve(
        ITemplateVariableExtractor extractor, string actionType, IReadOnlyDictionary<string, string> parameters, Models.Content content) =>
        Resolve(actionType, parameters, (template, encoding) => extractor.ResolveVariables(template, content, encoding));

    /// <summary>
    /// The same, after reading what the parameters name beyond the entry: the site's time zone, the
    /// author and the transition that fired. What a run goes through, since only a run knows its trigger.
    /// </summary>
    /// <remarks>
    /// The read is kept by the extractor for this entry, so a Conditional among the actions resolves
    /// its children against it without being handed anything more.
    /// </remarks>
    public static async Task<Dictionary<string, string>> ResolveAsync(
        ITemplateVariableExtractor extractor, string actionType, IReadOnlyDictionary<string, string> parameters,
        Models.Content content, string? triggerEvent, long eventSequence, CancellationToken ct)
    {
        await extractor.PrepareAsync(content, triggerEvent, eventSequence, parameters.Values, ct);
        return Resolve(extractor, actionType, parameters, content);
    }

    /// <summary>The same, without an extractor, for a caller that is not handed one.</summary>
    public static Dictionary<string, string> Resolve(
        string actionType, IReadOnlyDictionary<string, string> parameters, Models.Content content) =>
        Resolve(actionType, parameters, (template, encoding) => TemplateVariableExtractor.Resolve(template, content, encoding));

    private static Dictionary<string, string> Resolve(
        string actionType, IReadOnlyDictionary<string, string> parameters, Func<string, TemplateValueEncoding, string> resolve)
    {
        var resolved = new Dictionary<string, string>(parameters.Count);
        foreach (var (key, value) in parameters)
        {
            resolved[key] = IsResolvedByTheAction(actionType, key) ? value : resolve(value, EncodingFor(actionType, key));
        }

        return resolved;
    }
}

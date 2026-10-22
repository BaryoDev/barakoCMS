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
    ///
    /// A Publish's payload is left as written too. The action resolves each of its values on its own,
    /// so a quote in a field cannot rewrite the JSON, and checks the placeholders against its allowlist.
    ///
    /// So is the template an email names: which template is sent is the workflow's choice, never
    /// a value in the entry.
    ///
    /// An action can name more of its own with <c>SelfResolvedParameters</c> on its metadata, which
    /// the registry passed here reports. Callers without a registry only know the built-in ones.
    /// </remarks>
    public static bool IsResolvedByTheAction(string actionType, string parameter, IWorkflowPluginRegistry? registry = null) =>
        IsBuiltInResolvedByTheAction(actionType, parameter)
        || (registry?.GetActionMetadata(actionType)?.SelfResolvedParameters
            ?.Contains(parameter, StringComparer.OrdinalIgnoreCase) ?? false);

    private static bool IsBuiltInResolvedByTheAction(string actionType, string parameter) =>
        (actionType == "Conditional"
         && (parameter.Equals("Condition", StringComparison.OrdinalIgnoreCase)
             || parameter.Equals("ThenActions", StringComparison.OrdinalIgnoreCase)
             || parameter.Equals("ElseActions", StringComparison.OrdinalIgnoreCase)))
        || (actionType == "Publish" && parameter.Equals(Actions.PublishPayload.PayloadParameter, StringComparison.OrdinalIgnoreCase))
        || (actionType == "Email"
            && (parameter.Equals(Actions.EmailAction.AttachmentsParameter, StringComparison.OrdinalIgnoreCase)
                || parameter.Equals(EmailTemplates.EmailTemplateRenderer.TemplateParameter, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Whether the action reads <c>{{actions.N.name}}</c> in this parameter itself. An email's
    /// attachments do, to attach a file an earlier step produced; the others it resolves keep a
    /// reference as written.
    /// </summary>
    public static bool ReadsStepOutputsItself(string actionType, string parameter) =>
        actionType == "Email" && parameter.Equals(Actions.EmailAction.AttachmentsParameter, StringComparison.OrdinalIgnoreCase);

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
        ITemplateVariableExtractor extractor, string actionType, IReadOnlyDictionary<string, string> parameters, Models.Content content,
        IWorkflowPluginRegistry? registry = null) =>
        Resolve(actionType, parameters, content, (template, encoding) => extractor.ResolveVariables(template, content, encoding), registry);

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
        Models.Content content, string? triggerEvent, long eventSequence, CancellationToken ct,
        IWorkflowPluginRegistry? registry = null)
    {
        await extractor.PrepareAsync(content, triggerEvent, eventSequence, parameters.Values, ct);

        var recipient = actionType == "Email" && parameters.TryGetValue("To", out var to)
            ? extractor.ResolveVariables(to, content)
            : null;
        await extractor.ForRecipientAsync(recipient, content, ct);

        return Resolve(extractor, actionType, parameters, content, registry);
    }

    /// <summary>The same, without an extractor, for a caller that is not handed one.</summary>
    public static Dictionary<string, string> Resolve(
        string actionType, IReadOnlyDictionary<string, string> parameters, Models.Content content,
        IWorkflowPluginRegistry? registry = null) =>
        Resolve(actionType, parameters, content, (template, encoding) => TemplateVariableExtractor.Resolve(template, content, encoding), registry);

    /// <summary>
    /// The reserved parameter that names, one per line, the parameters whose template was one whole
    /// placeholder for a list or an object. Resolving sets it and drops any the workflow declared.
    /// </summary>
    public const string StructuredParameter = "StructuredParameters";

    private static readonly System.Text.RegularExpressions.Regex WholePlaceholder = new(
        @"\A\s*\{\{\s*data\.([A-Za-z0-9_]+)\s*(?:\|\s*json\s*)?\}\}\s*\z",
        System.Text.RegularExpressions.RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    /// <summary>Whether resolving marked <paramref name="parameter"/> as the JSON of a list or an object.</summary>
    public static bool IsStructured(IReadOnlyDictionary<string, string> parameters, string parameter) =>
        parameters.TryGetValue(StructuredParameter, out var marked)
        && marked.Split('\n').Contains(parameter, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Text is JSON for a list or an object only when the template was one whole placeholder and the
    /// field it names held a list or an object. A template that mixes JSON with a placeholder, or a
    /// text field that happens to hold JSON, is text: a value there could add keys.
    /// </summary>
    private static bool IsWholeStructure(string template, Models.Content content) =>
        WholePlaceholder.Match(template) is { Success: true } match
        && content.Data.TryGetValue(match.Groups[1].Value, out var value)
        && value switch
        {
            System.Text.Json.JsonElement element => element.ValueKind is System.Text.Json.JsonValueKind.Array or System.Text.Json.JsonValueKind.Object,
            string => false,
            System.Collections.IDictionary => true,
            System.Collections.IEnumerable => true,
            _ => false,
        };

    private static Dictionary<string, string> Resolve(
        string actionType, IReadOnlyDictionary<string, string> parameters, Models.Content content,
        Func<string, TemplateValueEncoding, string> resolve, IWorkflowPluginRegistry? registry)
    {
        var resolved = new Dictionary<string, string>(parameters.Count);
        var structured = new List<string>();
        foreach (var (key, value) in parameters)
        {
            if (key.Equals(StructuredParameter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var byTheAction = IsResolvedByTheAction(actionType, key, registry);
            resolved[key] = byTheAction ? value : resolve(value, EncodingFor(actionType, key));
            if (!byTheAction && IsWholeStructure(value, content))
            {
                structured.Add(key);
            }
        }

        if (structured.Count > 0)
        {
            resolved[StructuredParameter] = string.Join('\n', structured);
        }

        return resolved;
    }
}

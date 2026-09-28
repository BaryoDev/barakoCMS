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
    /// </summary>
    public static bool IsResolvedByTheAction(string actionType, string parameter) =>
        actionType == "Conditional"
        && (parameter.Equals("ThenActions", StringComparison.OrdinalIgnoreCase)
            || parameter.Equals("ElseActions", StringComparison.OrdinalIgnoreCase));

    public static Dictionary<string, string> Resolve(
        ITemplateVariableExtractor extractor, string actionType, IReadOnlyDictionary<string, string> parameters, Models.Content content) =>
        Resolve(actionType, parameters, (template, encoding) => extractor.ResolveVariables(template, content, encoding));

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

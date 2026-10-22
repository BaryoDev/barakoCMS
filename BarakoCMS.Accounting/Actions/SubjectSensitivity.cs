using System.Text.RegularExpressions;
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;

namespace BarakoCMS.Accounting;

/// <summary>
/// Keeps a sensitive field of the subject out of the entries the actions write. The actions declare
/// their text parameters as self-resolved, so they see <c>{{Data.Field}}</c> as written and refuse
/// one that names a field that is not Public, by name. Only then do they fill the rest in.
/// </summary>
internal static partial class SubjectSensitivity
{
    /// <summary>The parameters of <c>Accounting.PostJournal</c> that arrive as written.</summary>
    public static readonly string[] PostJournalParameters = ["Mapping", "ActionKey", "Memo", "Amount"];

    /// <summary>The parameters of <c>Accounting.Reverse</c> that arrive as written.</summary>
    public static readonly string[] ReverseParameters = ["ActionKey", "Reason"];

    /// <summary>Every <c>{{...}}</c> with no brace inside it, the way the template syntax finds one.</summary>
    [GeneratedRegex(@"\{\{([^{}]*)\}\}", RegexOptions.None, 2000)]
    private static partial Regex Hole();

    /// <summary>A field of the entry a hole reads: <c>data.Field</c>, <c>data.Field.Other</c>, in a loop or a format.</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_.])data\.(?<name>[A-Za-z0-9_.]+)", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex DataName();

    [GeneratedRegex(@"\{\{[^{}]*\bactions\.", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex OutputReference();

    /// <summary>The fields of the subject's type that are not Public, which an entry accounting users read must not carry.</summary>
    public static async Task<HashSet<string>> FieldsAsync(IQuerySession session, Content subject, CancellationToken ct)
    {
        var definition = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == subject.ContentType, ct);
        return (definition?.Fields ?? []).Where(f => f.Sensitivity != SensitivityLevel.Public)
            .Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Why a template may not be used: a placeholder in it reads a sensitive field. Only the name is
    /// looked at, in any casing and spacing, with a format or in a loop. What a value looks like says
    /// nothing about where it came from.
    /// </summary>
    public static string? Problem(string? template, HashSet<string> sensitive)
    {
        if (string.IsNullOrEmpty(template) || sensitive.Count == 0)
            return null;

        foreach (Match hole in Hole().Matches(template))
        {
            foreach (Match read in DataName().Matches(hole.Groups[1].Value))
            {
                var name = read.Groups["name"].Value.TrimEnd('.');
                foreach (var field in sensitive)
                {
                    if (name.Equals(field, StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith(field + ".", StringComparison.OrdinalIgnoreCase))
                        return $"A parameter shows the sensitive field '{field}', which an accounting entry must not carry.";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Checks each declared parameter by name, then fills it in the way the runner fills any other:
    /// the extractor the run prepared and no encoding. A reference to an earlier action's output is
    /// refused, since its value could be a sensitive field that no name check here can see.
    /// </summary>
    /// <returns>The parameters with the declared ones filled, or why they cannot be.</returns>
    public static Dictionary<string, string>? Fill(
        IReadOnlyDictionary<string, string> parameters, string[] declared, Content subject, HashSet<string> sensitive,
        ITemplateVariableExtractor? extractor, out string? problem)
    {
        problem = null;
        var filled = new Dictionary<string, string>(parameters);

        foreach (var key in parameters.Keys.Where(k => declared.Contains(k, StringComparer.OrdinalIgnoreCase)))
        {
            var raw = parameters[key];
            if (string.IsNullOrEmpty(raw))
                continue;

            if (Problem(raw, sensitive) is { } named)
            {
                problem = named;
                return null;
            }

            // An earlier action's output is refused, not filled: nothing here can tell which field it came from.
            if (OutputReference().IsMatch(raw))
            {
                problem = $"Parameter '{key}' reads an earlier action's output, and an accounting entry takes only the entry's own fields.";
                return null;
            }

            var text = extractor is null
                ? TemplateVariableExtractor.Resolve(raw, subject, TemplateValueEncoding.None)
                : extractor.ResolveVariables(raw, subject, TemplateValueEncoding.None);

            filled[key] = text;
        }

        return filled;
    }
}

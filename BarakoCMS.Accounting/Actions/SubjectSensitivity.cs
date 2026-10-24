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

    /// <summary>A loop marker in a hole, which walks the entries a reference field points at.</summary>
    [GeneratedRegex(@"[#/]\s*each\b", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex Loop();

    /// <summary>The fields of the subject's type that are not Public, which an entry accounting users read must not carry.</summary>
    public static async Task<HashSet<string>> FieldsAsync(IQuerySession session, Content subject, CancellationToken ct)
    {
        var definition = await barakoCMS.Infrastructure.Services.AccountContentTypes.ResolveAsync(session, subject.ContentType, ct);
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
    /// Whether a template reads past the entry's own fields: a loop, or a <c>data.</c> path of more
    /// than one segment, which follows a reference to another entry.
    /// </summary>
    public static bool ReadsReferencedEntry(string template)
    {
        foreach (Match hole in Hole().Matches(template))
        {
            var text = hole.Groups[1].Value;
            if (Loop().IsMatch(text))
                return true;

            foreach (Match read in DataName().Matches(text))
            {
                if (read.Groups["name"].Value.Contains('.'))
                    return true;
            }
        }

        return false;
    }

    /// <summary>A copy of the subject whose data has the sensitive keys removed, under the same id.</summary>
    public static Content Without(Content subject, HashSet<string> sensitive)
    {
        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, value) in subject.Data)
        {
            if (!sensitive.Contains(key))
                data[key] = value;
        }

        return new Content
        {
            Id = subject.Id,
            ContentType = subject.ContentType,
            Data = data,
            Status = subject.Status,
            Sensitivity = subject.Sensitivity,
            CreatedAt = subject.CreatedAt,
            UpdatedAt = subject.UpdatedAt,
            ScheduledPublishAt = subject.ScheduledPublishAt,
            ScheduledUnpublishAt = subject.ScheduledUnpublishAt,
            ScheduledSensitivity = subject.ScheduledSensitivity,
            ScheduledSensitivityAt = subject.ScheduledSensitivityAt,
            LastModifiedBy = subject.LastModifiedBy,
            LifecycleState = subject.LifecycleState,
            SourceRunId = subject.SourceRunId,
            CreatedBy = subject.CreatedBy,
            BranchId = subject.BranchId,
            SearchText = subject.SearchText,
        };
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
        var visible = Without(subject, sensitive);

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

            if (ReadsReferencedEntry(raw))
            {
                problem = $"Parameter '{key}' reads a referenced entry, and an accounting entry takes only the entry's own fields.";
                return null;
            }

            // Resolved against a copy without the sensitive keys, so no template syntax can fill one.
            var text = extractor is null
                ? TemplateVariableExtractor.Resolve(raw, visible, TemplateValueEncoding.None)
                : extractor.ResolveVariables(raw, visible, TemplateValueEncoding.None);

            filled[key] = text;
        }

        return filled;
    }
}

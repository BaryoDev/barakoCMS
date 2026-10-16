using barakoCMS.Models;
using Marten;

namespace BarakoCMS.Accounting;

/// <summary>
/// Keeps a sensitive field of the subject out of the entries the actions write. The workflow runner
/// fills a <c>{{Data.Field}}</c> in a parameter before the action runs, with no regard to
/// sensitivity, so what an action sees is the value, and the check looks for the value.
/// </summary>
internal static class SubjectSensitivity
{
    /// <summary>The fields of the subject's type that are not Public, which an entry accounting users read must not carry.</summary>
    public static async Task<HashSet<string>> FieldsAsync(IQuerySession session, Content subject, CancellationToken ct)
    {
        var definition = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == subject.ContentType, ct);
        return (definition?.Fields ?? []).Where(f => f.Sensitivity != SensitivityLevel.Public)
            .Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Why one of the texts may not be used: it names a sensitive field or carries its value. A short
    /// value can match by chance, and that errs on the side of refusing.
    /// </summary>
    public static string? Problem(IEnumerable<string?> texts, Content subject, HashSet<string> sensitive)
    {
        foreach (var field in sensitive)
        {
            var value = ContentData.AsString(ContentData.Get(subject.Data, field));
            foreach (var text in texts.Where(t => !string.IsNullOrEmpty(t)))
            {
                if (text!.Contains($"{{{{Data.{field}}}}}", StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrEmpty(value) && text.Contains(value, StringComparison.OrdinalIgnoreCase)))
                    return $"A parameter shows the sensitive field '{field}', which an accounting entry must not carry.";
            }
        }

        return null;
    }
}

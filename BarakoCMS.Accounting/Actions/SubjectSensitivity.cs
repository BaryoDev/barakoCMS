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

    /// <summary>
    /// Why an amount parameter may not be used: it names a sensitive field, or it is that field's
    /// number, which is what the runner leaves once it has filled <c>{{Data.Field}}</c> in. An amount
    /// that happens to equal a sensitive number is refused too.
    /// </summary>
    public static string? AmountProblem(string? amount, Content subject, HashSet<string> sensitive)
    {
        if (string.IsNullOrWhiteSpace(amount))
            return null;

        if (Problem([amount], subject, sensitive.Where(f => amount.Contains($"{{{{Data.{f}}}}}", StringComparison.OrdinalIgnoreCase)).ToHashSet()) is { } named)
            return named;

        if (!decimal.TryParse(amount, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var given))
            return null;

        foreach (var field in sensitive)
        {
            var raw = ContentData.Get(subject.Data, field);
            if (raw is null || raw is string { Length: 0 })
                continue;
            if (decimal.TryParse(Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var number) && number == given)
                return $"A parameter shows the sensitive field '{field}', which an accounting entry must not carry.";
        }

        return null;
    }
}

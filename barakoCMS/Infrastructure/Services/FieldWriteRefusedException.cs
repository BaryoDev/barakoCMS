namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// A write gave a value to a field the caller's permission rule does not let them set.
/// </summary>
/// <remarks>
/// Thrown from the write rule in <see cref="SensitivityService"/>, under whichever endpoint is
/// writing, and turned into a 403 carrying the message by <c>MalformedRequestMiddleware</c>, the way
/// <see cref="ReferenceConditionBoundException"/> is. The message names fields by the content type's
/// own spelling and only counts keys the type does not declare, which are whatever the request sent.
/// Nothing is written: the write rule runs before anything is staged, and a batch's transaction is
/// rolled back when the exception leaves it.
/// </remarks>
internal sealed class FieldWriteRefusedException(IReadOnlyList<string> fields, int undeclared) : InvalidOperationException(
    Describe(fields, undeclared))
{
    public IReadOnlyList<string> Fields { get; } = fields;

    private static string Describe(IReadOnlyList<string> fields, int undeclared)
    {
        var parts = new List<string>();
        if (fields.Count > 0)
            parts.Add(string.Join(", ", fields.Take(20).Select(f => $"'{f}'")) + (fields.Count > 20 ? $" and {fields.Count - 20} more" : string.Empty));
        if (undeclared > 0)
            parts.Add($"{undeclared} {(undeclared == 1 ? "field" : "fields")} the content type does not declare");

        return "The permission rule that grants this write does not let the caller set "
             + string.Join(", and ", parts)
             + ". Leave those fields out or send their stored values.";
    }
}

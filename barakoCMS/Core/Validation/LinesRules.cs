using barakoCMS.Models;

namespace barakoCMS.Core.Validation;

/// <summary>
/// What is wrong with a type's declared lines, whichever path stores them: the lines endpoint, a
/// blueprint, a config document, an import or a field change.
/// </summary>
internal static class LinesRules
{
    /// <summary>
    /// The first thing wrong with <paramref name="asked"/> as the lines of <paramref name="header"/>,
    /// or none. <paramref name="child"/> is the line type as it is or will be stored, null when no
    /// such type exists. <paramref name="all"/> is every type as it is or will be stored, when the
    /// caller can say, so a type that is already another's line type is refused lines of its own.
    /// </summary>
    public static List<string> Errors(
        ContentTypeDefinition header, ContentTypeDefinition? child, LinesDefinition asked,
        IEnumerable<ContentTypeDefinition>? all = null)
    {
        if (child is null)
        {
            return [$"Line type '{asked.ContentType}' does not exist."];
        }

        if (string.Equals(child.Name, header.Name, StringComparison.OrdinalIgnoreCase))
        {
            return ["A type cannot be its own line type."];
        }

        if (all?.FirstOrDefault(t => t.Lines is { } l && !ContentTypeName.Same(t.Name, header.Name)
                && ContentTypeName.Normalize(l.ContentType) == ContentTypeName.Normalize(header.Name)) is { } owner)
        {
            return [$"'{header.Name}' is the line type of '{owner.Name}', so it cannot have lines of its own. Lines are one level deep."];
        }

        if (child.Lines is not null)
        {
            return [$"Line type '{child.Name}' has lines of its own. Lines are one level deep."];
        }

        var parent = child.Fields.FieldNamed(asked.ParentField);
        if (parent is null)
        {
            return [$"Line type '{child.Name}' has no field '{asked.ParentField}'."];
        }

        if (!string.Equals(parent.Type, "reference", StringComparison.OrdinalIgnoreCase)
            || parent.Multiple
            || !string.Equals(parent.ReferenceType, header.Name, StringComparison.OrdinalIgnoreCase))
        {
            return [$"Field '{parent.Name}' of '{child.Name}' must be a single reference to '{header.Name}'."];
        }

        if ((asked.TotalField is null) != (asked.LineAmountField is null))
        {
            return ["totalField and lineAmountField are set together or not at all."];
        }

        if (asked.TotalField is not null)
        {
            var total = header.Fields.FieldNamed(asked.TotalField);
            var amount = child.Fields.FieldNamed(asked.LineAmountField);
            if (total is null || !IsNumber(total) || amount is null || !IsNumber(amount))
            {
                return [$"'{asked.TotalField}' on '{header.Name}' and '{asked.LineAmountField}' on '{child.Name}' must both be number fields."];
            }
        }

        return RollupFields.LinesErrors(header, child, asked).ToList();
    }

    /// <summary>
    /// <paramref name="asked"/> as it is stored: the line type's stored name and the stored spelling
    /// of each field. Only for lines <see cref="Errors"/> accepted.
    /// </summary>
    public static LinesDefinition Stored(ContentTypeDefinition header, ContentTypeDefinition child, LinesDefinition asked) => new()
    {
        ContentType = child.Name,
        ParentField = child.Fields.FieldNamed(asked.ParentField)!.Name,
        TotalField = asked.TotalField is null ? null : header.Fields.FieldNamed(asked.TotalField)!.Name,
        LineAmountField = asked.LineAmountField is null ? null : child.Fields.FieldNamed(asked.LineAmountField)!.Name,
    };

    private static bool IsNumber(FieldDefinition field) =>
        field.Type.ToLowerInvariant() is "int" or "decimal" or "money" or "integer" or "number";
}

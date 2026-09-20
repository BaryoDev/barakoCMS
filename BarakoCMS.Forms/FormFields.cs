using barakoCMS.Models;

namespace BarakoCMS.Forms;

/// <summary>Which fields of a content type an anonymous visitor may fill in.</summary>
internal static class FormFields
{
    /// <summary>
    /// Field types a form can draw as a plain input. Markdown, rich text, json, references, arrays,
    /// objects and geopoints are left out: a widget cannot render them for a stranger, and a
    /// reference would let one probe for entry ids.
    /// </summary>
    private static readonly HashSet<string> RenderableTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "string", "text", "int", "integer", "number", "decimal", "money",
        "bool", "boolean", "date", "datetime", "time", "email", "url", "choice",
    };

    /// <summary>
    /// Fields that are Public, of a renderable type, and not the slug. Anything else is refused on
    /// submit and never shown in the form definition.
    /// </summary>
    /// <remarks>
    /// The slug is excluded because the uniqueness check on it answers whether another entry already
    /// holds a value, and on a form that entry is somebody else's submission.
    /// </remarks>
    public static IReadOnlyList<FieldDefinition> Submittable(ContentTypeDefinition definition) =>
        definition.Fields
            .Where(f => f.Sensitivity == SensitivityLevel.Public
                     && RenderableTypes.Contains(f.Type)
                     && !string.Equals(f.Name, "slug", StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>Required fields a visitor cannot fill in, which make a type unusable as a form.</summary>
    public static IReadOnlyList<FieldDefinition> RequiredButNotSubmittable(ContentTypeDefinition definition)
    {
        var submittable = Submittable(definition);
        return definition.Fields.Where(f => f.IsRequired && !submittable.Contains(f)).ToList();
    }
}

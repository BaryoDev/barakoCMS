using barakoCMS.Models;

namespace BarakoCMS.Documents;

/// <summary>
/// Decides whether a template may be rendered for a content type. A document is an egress path like
/// the API, so a field that cannot leave over the API cannot leave in a PDF.
/// </summary>
public static class DocumentTemplateGuard
{
    internal static readonly string[] SystemVariables = ["id", "contenttype", "status", "createdat", "updatedat"];

    /// <summary>The reason this template may not be rendered for this schema, or null.</summary>
    public static string? Refuse(DocumentTemplate template, ContentTypeDefinition? schema)
    {
        var parsed = DocumentTemplateParser.Parse(template.Source);
        if (!parsed.Ok) return parsed.Errors[0];

        // Without the field definitions there is no telling a Public field from a Sensitive one.
        if (schema is null)
        {
            return $"There is no content type '{template.ContentType}', so its fields cannot be checked.";
        }

        var texts = parsed.Blocks.SelectMany(b => b.Cells).Append(template.FileName).ToList();

        var found = TemplateHoles.FirstNonPublic(texts, schema);
        if (found is not null)
        {
            return $"'{found.Value.Hole}' is {found.Value.Field.Sensitivity} on '{schema.Name}', and a document "
                 + "would carry it out of the system. A field that is not Public cannot leave, "
                 + "even when a template names it.";
        }

        foreach (var name in texts.SelectMany(TemplateHoles.Names))
        {
            if (TemplateHoles.FieldFor(name, schema) is null
                && !SystemVariables.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return $"'{{{{{name}}}}}' names no field on '{schema.Name}'.";
            }
        }

        return null;
    }
}

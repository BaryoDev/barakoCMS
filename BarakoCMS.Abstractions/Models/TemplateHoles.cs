using System.Text.RegularExpressions;

namespace barakoCMS.Models;

/// <summary>
/// The <c>{{name}}</c> holes a template names, and the one rule every template that sends an entry
/// somewhere shares: a field that is not Public cannot leave.
/// </summary>
/// <remarks>
/// Outbound requests and generated documents both call this, so the two cannot drift apart on what
/// may leave. Each caller words its own refusal.
/// </remarks>
public static class TemplateHoles
{
    private static readonly Regex Hole = new(@"\{\{\s*([A-Za-z0-9_.\[\]]+)\s*\}\}", RegexOptions.Compiled);

    /// <summary>The name inside each hole of <paramref name="template"/>, in order.</summary>
    public static IEnumerable<string> Names(string? template) =>
        Hole.Matches(template ?? string.Empty).Select(m => m.Groups[1].Value);

    /// <summary>The schema field a hole names, or null when it names a system variable or nothing.</summary>
    public static FieldDefinition? FieldFor(string name, ContentTypeDefinition schema)
    {
        // "data.Title" and "Title" both name the same field: the admin's variable picker offers the
        // first and templates in the wild use the second.
        var bare = name.StartsWith("data.", StringComparison.OrdinalIgnoreCase) ? name[5..] : name;

        return schema.Fields.FirstOrDefault(f => string.Equals(f.Name, bare, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The first hole in <paramref name="templates"/> that names a field of <paramref name="schema"/>
    /// whose sensitivity is not Public, or null when there is none.
    /// </summary>
    public static (string Hole, FieldDefinition Field)? FirstNonPublic(
        IEnumerable<string?> templates, ContentTypeDefinition schema)
    {
        foreach (var template in templates)
        {
            foreach (Match match in Hole.Matches(template ?? string.Empty))
            {
                var field = FieldFor(match.Groups[1].Value, schema);
                if (field is not null && field.Sensitivity != SensitivityLevel.Public)
                {
                    return (match.Value, field);
                }
            }
        }

        return null;
    }
}

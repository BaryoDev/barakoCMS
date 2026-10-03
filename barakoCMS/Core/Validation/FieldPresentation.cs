using barakoCMS.Models;

namespace barakoCMS.Core.Validation;

/// <summary>
/// What a field definition may say about how it is edited and what it is to its entry: the
/// <see cref="FieldDefinition.Editor"/> hint, the <see cref="FieldDefinition.Section"/> and the
/// <see cref="FieldDefinition.Role"/>, and the route template a content type may carry.
/// </summary>
/// <remarks>
/// None of these changes what an entry may hold. All are null on every definition stored before
/// they existed, and a reader here answers "none" for null, so such a type behaves as it did.
///
/// The two vocabularies are lists in this file and nowhere else. The type validator refuses a name
/// outside them, and <c>GET /api/meta/describe</c> reports them, so a console can tell what it may
/// be sent. Names are compared exactly: lower case is the one spelling stored.
///
/// Each name is tied to the field types that can hold what it means. A block list in a
/// <c>bool</c> field would be refused on every entry write, far from the definition that caused it,
/// so the definition is refused instead.
/// </remarks>
internal static class FieldPresentation
{
    /// <param name="Name">The value a field definition carries.</param>
    /// <param name="FieldTypes">The canonical field types it may be declared on.</param>
    public sealed record Spec(string Name, IReadOnlyList<string> FieldTypes);

    public const string TitleRole = "title";
    public const string SummaryRole = "summary";
    public const string DateRole = "date";

    public const int MaxSectionLength = 60;
    public const int MaxRouteTemplateLength = 200;
    public const string SlugToken = "{slug}";

    private static readonly string[] Lists = ["json", "array"];

    /// <summary>The editors a console is known to render, each for a value of these types.</summary>
    public static IReadOnlyList<Spec> Editors { get; } =
    [
        new("blocks", Lists),
        new("menu", Lists),
        new("links", Lists),
        new("image", ["url", "string", FileFields.TypeName]),
    ];

    public static IReadOnlyList<Spec> Roles { get; } =
    [
        new(TitleRole, ["string", "text"]),
        new(SummaryRole, ["string", "text", "markdown", "richtext"]),
        new(DateRole, ["date", "datetime"]),
    ];

    /// <summary>What is wrong with the editor, section and role one field declares.</summary>
    /// <remarks>
    /// A refused editor or role is not repeated in the message: it is not known to be a short word
    /// yet. The accepted names are listed instead.
    /// </remarks>
    public static List<string> DefinitionErrors(FieldDefinition field)
    {
        var errors = new List<string>();

        if (field.Editor is not null)
            errors.AddRange(NameErrors(field, "editor", field.Editor, Editors));

        if (field.Section is not null)
        {
            if (string.IsNullOrWhiteSpace(field.Section) || field.Section != field.Section.Trim())
            {
                errors.Add($"Field '{field.Name}' has a section that is blank, or starts or ends with a space. "
                    + "Leave section out for a field that sits in none.");
            }
            else if (field.Section.Length > MaxSectionLength)
            {
                errors.Add($"Field '{field.Name}' has a section longer than {MaxSectionLength} characters.");
            }
            else if (field.Section.Any(char.IsControl))
            {
                errors.Add($"Field '{field.Name}' has a section holding a control character.");
            }
        }

        if (field.Role is not null)
            errors.AddRange(NameErrors(field, "role", field.Role, Roles));

        return errors;
    }

    private static IEnumerable<string> NameErrors(
        FieldDefinition field, string noun, string declared, IReadOnlyList<Spec> accepted)
    {
        var spec = accepted.FirstOrDefault(s => string.Equals(s.Name, declared, StringComparison.Ordinal));

        if (spec is null)
        {
            yield return $"Field '{field.Name}' has a value for {noun} that is not accepted. Accepted values: "
                + $"{string.Join(", ", accepted.Select(s => s.Name))}.";
            yield break;
        }

        // An unknown type is reported by the type check, in its own words.
        if (FieldTypeRegistry.IsKnownType(field.Type)
            && !spec.FieldTypes.Contains(field.Type, StringComparer.OrdinalIgnoreCase))
        {
            yield return $"Field '{field.Name}' declares the {noun} '{spec.Name}' and is of type '{field.Type}'. "
                + $"That {noun} applies to a field of type {string.Join(" or ", spec.FieldTypes)}.";
        }
    }

    /// <summary>A role two fields of one type both claim. Readers take one field per role.</summary>
    public static List<string> RoleErrors(IEnumerable<FieldDefinition> fields)
    {
        var errors = new List<string>();

        foreach (var role in Roles)
        {
            var holders = fields
                .Where(f => f is not null && string.Equals(f.Role, role.Name, StringComparison.Ordinal))
                .Select(f => f.Name)
                .ToList();

            if (holders.Count > 1)
            {
                errors.Add($"The role '{role.Name}' is declared by {holders.Count} fields, '{holders[0]}' and "
                    + $"'{holders[1]}' among them. One field of a type holds a role.");
            }
        }

        return errors;
    }

    /// <summary>
    /// The field of the type that holds this role, or null. A stored role outside the vocabulary
    /// matches nothing, and of two stored holders the first in the type's order is read.
    /// </summary>
    public static string? FieldWithRole(ContentTypeDefinition? definition, string role) =>
        definition?.Fields
            .FirstOrDefault(f => f is not null && string.Equals(f.Role, role, StringComparison.Ordinal))
            ?.Name;

    /// <summary>
    /// The field names a reader tries in order for a role: the field declaring it, when the type
    /// has one, and then the names that were guessed before roles existed.
    /// </summary>
    public static string[] Candidates(ContentTypeDefinition? definition, string role, params string[] fallback)
    {
        if (FieldWithRole(definition, role) is not { } declared)
            return fallback;

        return [declared, .. fallback];
    }

    /// <summary>
    /// Is this a path the feed and the sitemap may join to the site URL: it starts with <c>/</c>,
    /// holds <c>{slug}</c> exactly once, is otherwise ASCII letters, digits and <c>- _ . ~ /</c>,
    /// and has no empty segment and no <c>.</c> or <c>..</c> segment.
    /// </summary>
    /// <remarks>
    /// The leading slash is what keeps the link on the configured host. Joined to
    /// <c>https://example.com</c>, a template of <c>@other.example/{slug}</c> would name another
    /// one. The stored value is also returned as it is, for a console or a renderer to use, and
    /// one that resolves it as a URL reference reads <c>//other.example/{slug}</c> as another host
    /// and <c>/../{slug}</c> as a path above the one written. So neither is a template. Readers
    /// ask this of a stored template too and ignore one that fails, so a value put in the database
    /// some other way is not served.
    /// </remarks>
    public static bool IsRouteTemplate(string? template)
    {
        if (string.IsNullOrEmpty(template) || template.Length > MaxRouteTemplateLength || template[0] != '/')
            return false;

        var at = template.IndexOf(SlugToken, StringComparison.Ordinal);
        if (at < 0 || template.IndexOf(SlugToken, at + SlugToken.Length, StringComparison.Ordinal) >= 0)
            return false;

        if (template.Contains("//", StringComparison.Ordinal)
            || template.Split('/').Any(segment => segment is "." or ".."))
        {
            return false;
        }

        return template
            .Remove(at, SlugToken.Length)
            .All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.' or '~');
    }

    /// <summary>What is wrong with the route template a type declares. Null is valid and means none.</summary>
    public static List<string> RouteTemplateErrors(string? template)
    {
        var errors = new List<string>();

        if (template is not null && !IsRouteTemplate(template))
        {
            errors.Add($"routeTemplate must be a path that starts with / and holds {SlugToken} once, such as "
                + $"/blog/{SlugToken}, of at most {MaxRouteTemplateLength} characters: letters, digits, "
                + "'-', '_', '.', '~' and '/', with no empty segment and no '.' or '..' segment. Leave it "
                + "out to use the configured path or the default.");
        }

        return errors;
    }
}

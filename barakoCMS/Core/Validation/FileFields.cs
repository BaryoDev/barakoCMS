using System.Text.Json;
using barakoCMS.Models;

namespace barakoCMS.Core.Validation;

/// <summary>
/// What a <c>file</c> field holds: the id of one stored file, as text.
/// </summary>
/// <remarks>
/// The entry stores the id and nothing else. The file's name, type, size, address and words are
/// read from the file store when an entry is answered, so they are never a second copy to keep in
/// step, and a file that is deleted or made private stops resolving without the entry changing.
///
/// The id is written with hyphens, 36 characters, the form <see cref="Guid.ToString()"/> gives.
/// The Files module finds what uses a file by looking for that text in an entry's data, so an id
/// written any other way would be a file in use that a delete does not see.
/// </remarks>
internal static class FileFields
{
    public const string TypeName = "file";

    public static bool IsFileField([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] FieldDefinition? field) =>
        field is not null && string.Equals(field.Type, TypeName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The names of the type's file fields, compared without case as data keys are.</summary>
    public static HashSet<string> Names(ContentTypeDefinition? definition) =>
        (definition?.Fields ?? [])
            .Where(IsFileField)
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads the id a file field holds, or false for anything that is not one.</summary>
    public static bool TryReadId(object? value, out Guid id) =>
        Guid.TryParseExact(Text(value), "D", out id);

    /// <summary>
    /// Whether the stored data already holds this value in this field, as the same text.
    /// </summary>
    /// <remarks>
    /// Text and not an id, so a field that was a <c>string</c> or a <c>url</c> before it became a
    /// file field keeps what its entries hold: a save that sends the stored value back is not a
    /// new attachment, whatever the value is.
    /// </remarks>
    public static bool Holds(IReadOnlyDictionary<string, object> stored, string fieldName, object? value)
    {
        if (Text(value) is not { } text)
            return false;

        foreach (var (key, held) in stored)
        {
            if (string.Equals(key, fieldName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Text(held), text, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? Text(object? value) => value switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
        _ => null,
    };

    /// <summary>
    /// The one answer for every value a write refuses: text that is not an id, an id no file has,
    /// a file of another tenant, a cached resize, and a private file the caller may not download.
    /// </summary>
    /// <remarks>
    /// One message, and the value is not repeated in it, so a write cannot be used to learn which
    /// ids name a file.
    /// </remarks>
    public static string Refused(FieldDefinition field) =>
        $"Field '{field.DisplayName}' takes the id of a stored file you may use, and this value is not one.";

    public static string NoStore(FieldDefinition field) =>
        $"Field '{field.DisplayName}' holds a stored file, and no module that stores files is enabled. "
        + "Enable BarakoCMS.Files and restart.";
}

using barakoCMS.Models;

// In the global namespace so every file that looks a field up can call it unqualified.

/// <summary>Finds a field of a definition by name, ignoring case.</summary>
internal static class FieldNameLookup
{
    public static FieldDefinition? FieldNamed(this IEnumerable<FieldDefinition>? fields, string? name) =>
        fields?.FirstOrDefault(f => f is not null && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
}

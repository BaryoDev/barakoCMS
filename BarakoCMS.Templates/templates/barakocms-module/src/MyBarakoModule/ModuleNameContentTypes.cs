using barakoCMS.Models;

namespace MyBarakoModule;

/// <summary>
/// The content type this module seeds. A note is ordinary barakoCMS content, so it is stored per
/// tenant, permissioned and delivered through the generic endpoints like any other entry, and the
/// module needs no document type or storage library of its own.
/// </summary>
public static class ModuleNameContentTypes
{
    /// <summary>The type name, matched exactly: core stores it as the caller spelled it.</summary>
    public const string Note = "modulename-note";

    public static ContentTypeDefinition NoteDefinition() => new()
    {
        Id = Guid.Parse("6f0b7a52-3c1d-4e8a-9b24-5d7e1a0c8f33"),
        Name = Note,
        DisplayName = "Note",
        Description = "A short note.",
        Fields =
        [
            new() { Name = "Title", Type = "string", IsRequired = true },
        ],
    };
}

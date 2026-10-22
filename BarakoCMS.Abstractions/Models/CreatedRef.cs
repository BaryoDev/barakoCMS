namespace barakoCMS.Models;

/// <summary>An entry a workflow action created, kept on the attempt so a run shows what it made.</summary>
/// <param name="ContentType">The created entry's type.</param>
/// <param name="Id">The created entry's id.</param>
/// <param name="Number">The number the entry was given, or null when its type has no number series.</param>
public sealed record CreatedRef(string ContentType, Guid Id, string? Number);

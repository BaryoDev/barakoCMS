using barakoCMS.Models;

namespace barakoCMS.Features.Collections.Push;

internal sealed class Request
{
    /// <summary>The content type, from the route.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Each entry's field values, keyed by field name. The type's slug field is the key.</summary>
    public List<Dictionary<string, object>> Entries { get; set; } = new();

    /// <summary>
    /// Archive every published entry of the type whose slug is not in this push, once every write
    /// in it has succeeded.
    /// </summary>
    public bool ArchiveMissing { get; set; }

    /// <summary>The status a created entry starts at, and an updated one is left at.</summary>
    public ContentStatus Status { get; set; } = ContentStatus.Published;
}

internal sealed class Response
{
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int Archived { get; set; }

    /// <summary>Why each refused entry was refused. Any entry here means nothing was written.</summary>
    public List<EntryError> Errors { get; set; } = new();
}

internal sealed class EntryError
{
    /// <summary>The entry's position in the request, from zero.</summary>
    public int Index { get; set; }

    public string? Slug { get; set; }

    public List<string> Messages { get; set; } = new();
}

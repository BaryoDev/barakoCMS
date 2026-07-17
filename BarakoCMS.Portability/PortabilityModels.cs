using barakoCMS.Models;

namespace BarakoCMS.Portability;

/// <summary>A portable snapshot of content-type definitions and their content data.</summary>
public class PortabilityBundle
{
    public int Version { get; set; } = 1;
    public DateTime ExportedAt { get; set; } = DateTime.UtcNow;
    public List<ContentTypeDefinition> ContentTypes { get; set; } = new();
    public List<ContentRecord> Contents { get; set; } = new();
}

/// <summary>One content item, stripped of instance-specific ids so it can be recreated anywhere.</summary>
public class ContentRecord
{
    public string ContentType { get; set; } = string.Empty;
    public Dictionary<string, object> Data { get; set; } = new();
    public string Status { get; set; } = "Published";
}

public class ImportRequest
{
    /// <summary>When true, report what would happen without writing anything.</summary>
    public bool DryRun { get; set; }
    public List<ContentTypeDefinition> ContentTypes { get; set; } = new();
    public List<ContentRecord> Contents { get; set; } = new();
}

public class ImportReport
{
    public bool DryRun { get; set; }
    public int ContentTypesCreated { get; set; }
    public int ContentTypesUpdated { get; set; }
    public int ContentsCreated { get; set; }
}

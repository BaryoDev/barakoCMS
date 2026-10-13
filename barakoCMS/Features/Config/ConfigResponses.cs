using System.Text.Json;
using System.Text.Json.Serialization;
using barakoCMS.Core.Interfaces;

namespace barakoCMS.Features.Config;

/// <summary>A tenant's configuration as one document, which <c>POST /api/config/apply</c> takes back.</summary>
internal sealed class ConfigExportResponse
{
    public int Version { get; init; } = ConfigDocument.CurrentVersion;

    public DateTime ExportedAt { get; init; }

    /// <summary>Per section, what is stored and could not be exported.</summary>
    public Dictionary<string, List<string>> Gaps { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The sections left out because the caller lacks the capability each one asks for.</summary>
    public List<string> Withheld { get; init; } = [];

    /// <summary>The sections, each at the top level under its own key.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Sections { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>What a document would do, or did, section by section.</summary>
internal sealed class ConfigPlanResponse
{
    /// <summary>Whether the changes were committed. A plan never is.</summary>
    public bool Applied { get; set; }

    public bool HasDestructiveChanges => Sections.Any(s => s.Changes.Any(c => c.Kind == ConfigChangeResponse.DestructiveKind));

    /// <summary>Nothing would change. What a second plan after an apply says.</summary>
    public bool IsEmpty => Sections.All(s => s.Changes.Count == 0);

    /// <summary>Why the document itself cannot be read.</summary>
    public List<string> Errors { get; init; } = [];

    /// <summary>What this caller may not do that the document asks for.</summary>
    public List<string> Refusals { get; init; } = [];

    public List<ConfigSectionPlanResponse> Sections { get; init; } = [];

    internal bool HasSectionErrors => Sections.Any(s => s.Errors.Count > 0);

    internal bool HasRefusals => Refusals.Count > 0 || Sections.Any(s => s.Refusals.Count > 0);
}

internal sealed class ConfigSectionPlanResponse
{
    public string Section { get; init; } = string.Empty;
    public List<ConfigChangeResponse> Changes { get; init; } = [];
    public List<string> Errors { get; init; } = [];
    public List<string> Refusals { get; init; } = [];
    public List<string> Notes { get; init; } = [];

    public static ConfigSectionPlanResponse From(ConfigSectionPlan plan) => new()
    {
        Section = plan.Section,
        Changes = plan.Changes.Select(ConfigChangeResponse.From).ToList(),
        Errors = plan.Errors.ToList(),
        Refusals = plan.Refusals.ToList(),
        Notes = plan.Notes.ToList(),
    };
}

internal sealed class ConfigChangeResponse
{
    public const string DestructiveKind = "destructive";

    public string Key { get; init; } = string.Empty;

    /// <summary><c>create</c>, <c>change</c> or <c>destructive</c>.</summary>
    public string Kind { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;
    public List<string> Details { get; init; } = [];

    public static ConfigChangeResponse From(ConfigChange change) => new()
    {
        Key = change.Key,
        Kind = change.Kind switch
        {
            ConfigChangeKind.Create => "create",
            ConfigChangeKind.Change => "change",
            _ => DestructiveKind,
        },
        Summary = change.Summary,
        Details = change.Details.ToList(),
    };
}

using barakoCMS.Models;

namespace barakoCMS.Features.Collections;

/// <summary>
/// A collection sync as the API describes it.
/// </summary>
/// <remarks>
/// Its own type rather than the stored document, for the reason <c>ResourceContractTests</c>
/// enforces: a stored property added later would otherwise publish itself to every client the moment
/// it was saved.
///
/// Nothing here can hold a credential. The connector holds it, encrypted, and this does not even
/// name the connector: it names a request definition, which names the connector.
/// </remarks>
internal sealed class CollectionSyncResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public string Source { get; init; } = nameof(SyncSource.Request);
    public string? RequestSlug { get; init; }
    public string? FeedUrl { get; init; }
    public string ItemsPath { get; init; } = string.Empty;
    public Dictionary<string, string> FieldMap { get; init; } = new();
    public Dictionary<string, SyncFieldRule> FieldRules { get; init; } = new();
    public List<SyncExcludeRule> Exclude { get; init; } = new();
    public bool ArchiveMissing { get; init; }
    public string KeyField { get; init; } = string.Empty;
    public List<string> FloorFields { get; init; } = new();
    public int IntervalMinutes { get; init; }
    public int MaxEntries { get; init; }
    public string EntryStatus { get; init; } = nameof(ContentStatus.Published);
    public DateTime? LastRunAt { get; init; }
    public DateTime? LastSuccessAt { get; init; }
    public int LastEntryCount { get; init; }

    /// <summary>Why the last run failed, or null. This is what an operator is shown.</summary>
    public string? LastError { get; init; }

    public int ConsecutiveFailures { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    public static CollectionSyncResponse From(CollectionSync s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Slug = s.Slug,
        ContentType = s.ContentType,
        Enabled = s.Enabled,
        Source = s.Source.ToString(),
        RequestSlug = s.RequestSlug,
        FeedUrl = s.FeedUrl,
        ItemsPath = s.ItemsPath,
        FieldMap = s.FieldMap,
        FieldRules = s.FieldRules ?? new(),
        Exclude = s.Exclude ?? new(),
        ArchiveMissing = s.ArchiveMissing,
        KeyField = s.KeyField,
        FloorFields = s.FloorFields,
        IntervalMinutes = s.IntervalMinutes,
        MaxEntries = s.MaxEntries,
        EntryStatus = s.EntryStatus.ToString(),
        LastRunAt = s.LastRunAt,
        LastSuccessAt = s.LastSuccessAt,
        LastEntryCount = s.LastEntryCount,
        LastError = s.LastError,
        ConsecutiveFailures = s.ConsecutiveFailures,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
    };
}

internal sealed class SaveCollectionSyncRequest
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    /// <summary>"Request" or "Feed".</summary>
    public string Source { get; set; } = nameof(SyncSource.Request);

    public string? RequestSlug { get; set; }
    public string? FeedUrl { get; set; }
    public string ItemsPath { get; set; } = string.Empty;

    /// <summary>Content field name to the dotted source path it reads.</summary>
    public Dictionary<string, string> FieldMap { get; set; } = new();

    /// <summary>Content field name to a rule that builds its value. See <see cref="SyncFieldRule"/>.</summary>
    public Dictionary<string, SyncFieldRule>? FieldRules { get; set; } = new();

    /// <summary>Rules that skip an item before it is mapped.</summary>
    public List<SyncExcludeRule>? Exclude { get; set; } = new();

    /// <summary>Archive this sync's entries a complete run did not produce. Off unless set.</summary>
    public bool ArchiveMissing { get; set; }

    public string KeyField { get; set; } = string.Empty;
    public List<string> FloorFields { get; set; } = new();
    public int IntervalMinutes { get; set; } = 60;
    public int MaxEntries { get; set; } = 100;
    public string EntryStatus { get; set; } = nameof(ContentStatus.Published);
}

/// <summary>What one run did, for the operator who pressed the button.</summary>
internal sealed class RunCollectionSyncResponse
{
    public bool Succeeded { get; init; }
    public int Created { get; init; }
    public int Updated { get; init; }
    public int Unchanged { get; init; }
    public int Skipped { get; init; }

    /// <summary>Items an exclude rule skipped. Not a fault, unlike <see cref="Skipped"/>.</summary>
    public int Excluded { get; init; }

    /// <summary>Entries archived because a complete run no longer produced them.</summary>
    public int Archived { get; init; }

    /// <summary>Why it failed, or null. Never a response body from the provider.</summary>
    public string? Error { get; init; }
}

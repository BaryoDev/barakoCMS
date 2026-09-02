using barakoCMS.Models;

namespace barakoCMS.Features.ContentType;

/// <summary>A content type as the API describes it, rather than as it is stored.</summary>
/// <remarks>
/// See <c>Features/Roles/RoleResponse</c> for the reasoning.
///
/// <see cref="FieldDefinition"/> is deliberately still passed through rather than mapped. It is the
/// schema the admin edits and the delivery layer enforces, so it is contract on purpose rather than
/// by accident, and duplicating it here would give two definitions of a field to keep in step. The
/// rule this box is about is that a resource's shape should be a decision; for fields, it is.
/// </remarks>
internal sealed class ContentTypeResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public List<FieldDefinition> Fields { get; init; } = new();

    /// <summary>This type's own states and named transitions, or null for Draft, Published, Archived.</summary>
    /// <remarks>
    /// Passed through for the same reason as <see cref="FieldDefinition"/>: it is what the admin
    /// designs and what the API enforces, so it is contract on purpose. Without it nothing outside
    /// the database can discover a type's transitions, and a transition is what a permission and a
    /// workflow trigger both name.
    /// </remarks>
    public LifecycleDefinition? Lifecycle { get; init; }

    public bool IsPubliclyDeliverable { get; init; }

    /// <summary>
    /// Whether the stream is the source of truth for entries of this type, and permanent either way.
    /// </summary>
    /// <remarks>
    /// Read from the type's sourcing policy rather than from the definition, because the decision
    /// belongs to the name and outlives the definition. A definition with no policy behind it is not
    /// event sourced, which is every type created before the policy existed.
    /// </remarks>
    public bool EventSourced { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public static ContentTypeResponse From(ContentTypeDefinition d, bool eventSourced = false) => new()
    {
        Id = d.Id,
        Name = d.Name,
        DisplayName = d.DisplayName,
        Description = d.Description,
        Fields = d.Fields,
        Lifecycle = d.Lifecycle,
        IsPubliclyDeliverable = d.IsPubliclyDeliverable,
        EventSourced = eventSourced,
        CreatedAt = d.CreatedAt,
        UpdatedAt = d.UpdatedAt,
    };
}

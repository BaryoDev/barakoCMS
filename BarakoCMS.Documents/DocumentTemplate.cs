namespace BarakoCMS.Documents;

/// <summary>The template one content type's entries are rendered with.</summary>
public sealed class DocumentTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>What a workflow action names, for example <c>invoice</c>.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>The content type this template renders. An entry of any other type is refused.</summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>The template language, see <see cref="DocumentTemplateParser"/>.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>The stored file's name. May hold holes. Empty means the slug.</summary>
    public string FileName { get; set; } = string.Empty;
}

/// <summary>
/// Remembers that a run already produced its document, so a retry returns the same file instead of
/// storing a second one.
/// </summary>
public sealed class DocumentRender
{
    /// <summary>The template id and the run's idempotency key, see <see cref="KeyFor"/>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Empty while the run holds the claim and has not stored its file yet.</summary>
    public Guid FileId { get; set; }

    public DateTimeOffset ClaimedAt { get; set; } = DateTimeOffset.UtcNow;

    public static string KeyFor(Guid templateId, string idempotencyKey) => $"{templateId:N}:{idempotencyKey}";
}

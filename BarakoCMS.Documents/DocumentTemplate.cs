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
/// storing a second one. Two kinds of row share the type, and neither is ever updated:
/// the result (<see cref="KeyFor"/>, written once, holds the file) and a claim
/// (<see cref="ClaimKeyFor"/>, one per attempt, holds no file).
/// </summary>
public sealed class DocumentRender
{
    /// <summary>The template id and the run's idempotency key, see <see cref="KeyFor"/>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Empty on a claim.</summary>
    public Guid FileId { get; set; }

    /// <summary>When the claim was made. A claim with no result after the claim lifetime is abandoned.</summary>
    public DateTimeOffset ClaimedAt { get; set; } = DateTimeOffset.UtcNow;

    public static string KeyFor(Guid templateId, string idempotencyKey) => $"{templateId:N}:{idempotencyKey}";

    /// <summary>
    /// The claim a run holds on <paramref name="resultKey"/>. Generation 0 is the first run. A run that
    /// finds generation n abandoned takes n+1. A claim is only ever inserted, never overwritten, so
    /// exactly one run owns a generation and the database decides which.
    /// </summary>
    public static string ClaimKeyFor(string resultKey, int generation) => $"{resultKey}#claim{generation}";
}

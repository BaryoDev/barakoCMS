using Marten;

namespace barakoCMS.Core.Interfaces;

/// <summary>
/// What a hook is given: the entry being written, and whether this is a create or an update.
/// <see cref="Data"/> is the live dictionary that will be persisted — a hook may enrich it (assign a
/// derived field, stamp a sequence number) as well as reject the write.
/// </summary>
public sealed class ContentLifecycleContext
{
    public required string ContentType { get; init; }
    public required Dictionary<string, object> Data { get; init; }

    /// <summary>The entry's existing data on update; null on create.</summary>
    public IReadOnlyDictionary<string, object>? Existing { get; init; }

    /// <summary>
    /// The request's Marten session — the same scoped instance the endpoint will commit. Rules use it
    /// to read other documents ("does this account exist?"), and anything a hook stores through it
    /// commits atomically with the content write, which is what lets a hook safely allocate a
    /// sequence number without a second transaction that could leave a gap.
    /// </summary>
    public required IDocumentSession Session { get; init; }

    public required Guid UserId { get; init; }

    public bool IsCreate => Existing is null;
}

/// <summary>
/// A per-content-type rule that runs inside the generic content write pipeline, so a domain with real
/// invariants doesn't need its own bespoke endpoint to be modelled as a content type.
///
/// This is the mechanism that makes "if it can be a content type, it should be" hold for domains
/// like accounting: the schema validator can express "Amount is a decimal", but not "total debits
/// must equal total credits" or "assign the next sequential entry number". Those live here, next to
/// the module that owns them, while the entry itself stays ordinary content — queryable, permissioned
/// and delivered through the same generic endpoints as everything else.
///
/// Hooks are registered in DI like workflow actions (<c>services.AddScoped&lt;IContentLifecycleHook,
/// MyHook&gt;()</c>), so a module contributes rules without core knowing the module exists. Every hook
/// whose <see cref="ContentType"/> matches the entry being written runs before it is persisted;
/// returning any error aborts the write.
/// </summary>
public interface IContentLifecycleHook
{
    /// <summary>The content type this hook guards, matched case-insensitively.</summary>
    string ContentType { get; }

    /// <summary>
    /// Validate and optionally enrich <see cref="ContentLifecycleContext.Data"/>. Return an empty
    /// list to allow the write; any returned message rejects it and is surfaced to the caller.
    /// </summary>
    Task<IReadOnlyList<string>> OnBeforeSaveAsync(ContentLifecycleContext context, CancellationToken ct);
}

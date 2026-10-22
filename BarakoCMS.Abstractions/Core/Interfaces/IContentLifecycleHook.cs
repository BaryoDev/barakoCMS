using Marten;

namespace barakoCMS.Core.Interfaces;

/// <summary>
/// What a hook is given: the entry being written, and whether this is a create or an update.
/// <see cref="Data"/> is the live dictionary that will be persisted — a hook may enrich it (assign a
/// derived field, stamp a sequence number) as well as reject the write.
/// </summary>
public sealed class ContentLifecycleContext : IContentHookContext
{
    public required string ContentType { get; init; }
    public required Dictionary<string, object> Data { get; init; }

    /// <summary>The entry's existing data on update; null on create.</summary>
    public IReadOnlyDictionary<string, object>? Existing { get; init; }

    /// <summary>
    /// The id of the entry being written; null on create, because the id does not exist until the
    /// write is accepted. A rule about the entry's own identity needs this: a parent reference cannot
    /// be checked for self-reference, nor its ancestor chain walked for a cycle, from the data alone.
    /// </summary>
    public Guid? EntryId { get; init; }

    /// <summary>
    /// The request's Marten session — the same scoped instance the endpoint will commit. Rules use it
    /// to read other documents ("does this account exist?"), and anything a hook stores through it
    /// commits atomically with the content write, which is what lets a hook safely allocate a
    /// sequence number without a second transaction that could leave a gap.
    /// </summary>
    [Obsolete("Use IContentHookContext.Reader, which names no Marten type. Removal planned for barakoCMS 6.0.0.")]
    public required IDocumentSession Session { get; init; }

    public required Guid UserId { get; init; }

    public bool IsCreate => Existing is null;

    IContentReader IContentHookContext.Reader =>
#pragma warning disable CS0618
        _reader ??= new SessionContentReader(Session);
#pragma warning restore CS0618

    private IContentReader? _reader;
    /// <summary>
    /// The lifecycle transition this write is, with its name and the states it moves between; null
    /// for a plain save. A hook picks its checks from the real move instead of a client-sent field.
    /// </summary>
    public ContentTransition? Transition { get; init; }
}

/// <summary>A lifecycle transition in progress: its declared name, and the states it leaves and enters.</summary>
public sealed record ContentTransition(string Name, string? FromState, string ToState);

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
/// returning any error aborts the write. The writes that run hooks are the content create, update and
/// rollback endpoints, and the <c>UpdateField</c> workflow action when it sets a data field.
/// </summary>
public interface IContentLifecycleHook
{
    /// <summary>
    /// The content type this hook guards, matched case-insensitively, or <see cref="AnyType"/> for a
    /// hook that decides per write which types it guards.
    /// </summary>
    string ContentType { get; }

    /// <summary>
    /// The <see cref="ContentType"/> of a hook run for every type, which then returns no errors for
    /// a type it does not guard. For a rule whose type is tenant data rather than code.
    /// </summary>
    const string AnyType = "*";

    /// <summary>
    /// Validate and optionally enrich <see cref="ContentLifecycleContext.Data"/>. Return an empty
    /// list to allow the write; any returned message rejects it and is surfaced to the caller.
    /// </summary>
    [Obsolete("Override BeforeSaveAsync(IContentHookContext, CancellationToken), which names no Marten type. "
              + "Removal planned for barakoCMS 6.0.0.")]
    Task<IReadOnlyList<string>> OnBeforeSaveAsync(ContentLifecycleContext context, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    /// <summary>
    /// The same check through barakoCMS types only. This is the member the host calls.
    /// </summary>
    /// <remarks>
    /// The default calls <see cref="OnBeforeSaveAsync"/>, so a hook written before this existed runs
    /// unchanged. A new hook overrides this one and leaves the other alone.
    /// </remarks>
    Task<IReadOnlyList<string>> BeforeSaveAsync(IContentHookContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context is not ContentLifecycleContext host)
        {
            throw new NotSupportedException(
                "This hook implements the Marten-typed OnBeforeSaveAsync, which needs the host's own context.");
        }

#pragma warning disable CS0618 // the default exists so a hook written before this member still runs
        return OnBeforeSaveAsync(host, ct);
#pragma warning restore CS0618
    }
}

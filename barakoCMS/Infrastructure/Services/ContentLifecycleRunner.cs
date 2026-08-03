using barakoCMS.Core.Interfaces;
using Marten;

namespace barakoCMS.Infrastructure.Services;

public interface IContentLifecycleRunner
{
    /// <summary>
    /// Runs every <see cref="IContentLifecycleHook"/> registered for <paramref name="contentType"/>,
    /// in registration order, before the entry is written. Hooks may enrich <paramref name="data"/>
    /// in place. Returns every error collected; a non-empty result must abort the write.
    /// </summary>
    Task<IReadOnlyList<string>> RunBeforeSaveAsync(
        string contentType,
        Dictionary<string, object> data,
        IReadOnlyDictionary<string, object>? existing,
        Guid userId,
        CancellationToken ct);
}

/// <summary>
/// Dispatches content lifecycle hooks. Kept separate from <see cref="IContentValidatorService"/>
/// deliberately: that service answers "does this match the declared schema", which is a pure
/// structural question, whereas a hook encodes a domain invariant and may legitimately mutate the
/// entry (stamping a sequence number, deriving a field). Conflating them would also have meant
/// changing the validator's signature everywhere it is already called.
///
/// All hooks for a type run even if an earlier one fails, so the caller sees every problem at once
/// rather than fixing them one round-trip at a time.
/// </summary>
public class ContentLifecycleRunner : IContentLifecycleRunner
{
    private readonly IEnumerable<IContentLifecycleHook> _hooks;
    private readonly IDocumentSession _session;

    public ContentLifecycleRunner(IEnumerable<IContentLifecycleHook> hooks, IDocumentSession session)
    {
        _hooks = hooks;
        _session = session;
    }

    public async Task<IReadOnlyList<string>> RunBeforeSaveAsync(
        string contentType,
        Dictionary<string, object> data,
        IReadOnlyDictionary<string, object>? existing,
        Guid userId,
        CancellationToken ct)
    {
        var matching = _hooks
            .Where(h => string.Equals(h.ContentType, contentType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matching.Count == 0)
            return Array.Empty<string>();

        var context = new ContentLifecycleContext
        {
            ContentType = contentType,
            Data = data,
            Existing = existing,
            Session = _session,
            UserId = userId,
        };

        var errors = new List<string>();
        foreach (var hook in matching)
            errors.AddRange(await hook.OnBeforeSaveAsync(context, ct));

        return errors;
    }
}

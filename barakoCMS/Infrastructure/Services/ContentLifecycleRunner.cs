using barakoCMS.Core.Interfaces;
using Marten;

namespace barakoCMS.Infrastructure.Services;

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
public class ContentLifecycleRunner(
    IEnumerable<IContentLifecycleHook> hooks,
    IDocumentSession session) : IContentLifecycleRunner
{
    public async Task<IReadOnlyList<string>> RunBeforeSaveAsync(
        string contentType,
        Guid? entryId,
        Dictionary<string, object> data,
        IReadOnlyDictionary<string, object>? existing,
        Guid userId,
        CancellationToken ct)
    {
        var matching = hooks
            .Where(h => h.ContentType == IContentLifecycleHook.AnyType
                || string.Equals(h.ContentType, contentType, StringComparison.OrdinalIgnoreCase))
            .ToList();

#pragma warning disable CS0618 // the host still fills the Marten session for hooks written before the reader
        var context = new ContentLifecycleContext
        {
            ContentType = contentType,
            EntryId = entryId,
            Data = data,
            Existing = existing,
            Session = session,
            UserId = userId,
        };
#pragma warning restore CS0618

        var errors = new List<string>();
        foreach (var hook in matching)
            errors.AddRange(await hook.BeforeSaveAsync(context, ct));

        // A parent the type declares as data, after the hooks code registered, so a module's own
        // rule about the same tree answers first.
        var lowered = contentType.ToLower();
        var definition = await session.Query<barakoCMS.Models.ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name.ToLower() == lowered, ct);
        errors.AddRange(await barakoCMS.Core.Validation.ParentFields.CheckAsync(definition, context, ct));

        return errors;
    }
}

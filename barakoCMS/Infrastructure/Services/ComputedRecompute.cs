using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Works out the computed values of a type's entries again when a definition write changes a
/// formula. Public because the Portability import is a module and starts it for a bundle.
/// </summary>
public static class ComputedRecompute
{
    /// <summary>Whether <paramref name="after"/> holds a formula that is new or different from the one in <paramref name="before"/>.</summary>
    public static bool Needed(IEnumerable<FieldDefinition>? before, ContentTypeDefinition after) =>
        FieldChanges.ChangedFormulas(before, after.Fields).Count > 0;

    /// <summary>
    /// Starts the backfill on <paramref name="session"/>, so it commits with the definition. Returns
    /// the refusal when another field change is already running on the type, or null.
    /// </summary>
    public static async Task<string?> StartAsync(
        IDocumentSession session, ContentTypeDefinition after, IEnumerable<FieldDefinition>? before, Guid requestedBy, CancellationToken ct) =>
        await FieldChanges.RecomputeAsync(session, after, before, requestedBy, ct) is { } running ? FieldChanges.Busy(running) : null;
}

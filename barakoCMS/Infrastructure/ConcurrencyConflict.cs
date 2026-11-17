namespace barakoCMS;

/// <summary>Whether an exception from a save is a lost optimistic-concurrency race.</summary>
/// <remarks>
/// Matched by name as well as type, since Marten surfaces the same conflict under more than one
/// exception type depending on the version and the kind of write.
/// </remarks>
internal static class ConcurrencyConflict
{
    internal static bool Is(Exception ex) =>
        ex is JasperFx.ConcurrencyException || ex.GetType().Name.Contains("Concurrency");
}

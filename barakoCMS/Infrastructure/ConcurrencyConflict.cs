namespace barakoCMS;

/// <summary>Whether an exception from a save is a lost optimistic-concurrency race.</summary>
/// <remarks>
/// Matched by name as well as type, since Marten surfaces the same conflict under more than one
/// exception type depending on the version and the kind of write. When more than one document in a
/// save lost, it reports an AggregateException of conflicts, which counts too.
/// </remarks>
internal static class ConcurrencyConflict
{
    internal static bool Is(Exception ex) =>
        ex is JasperFx.ConcurrencyException
        || ex.GetType().Name.Contains("Concurrency")
        || ex is AggregateException { InnerExceptions.Count: > 0 } many && many.InnerExceptions.All(Is);
}

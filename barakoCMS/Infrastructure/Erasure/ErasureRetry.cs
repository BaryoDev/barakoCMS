using Marten;

namespace barakoCMS.Infrastructure.Erasure;

/// <summary>
/// Runs an erasure again when a workflow runner saved one of the rows it touches in between.
/// </summary>
/// <remarks>
/// Runs use optimistic concurrency and the cascade loads and rewrites them, so a runner save between
/// that load and the commit throws. Nothing was written, so the pending work is dropped and the whole
/// attempt starts again from a fresh load. After <see cref="Attempts"/> tries the exception is not
/// swallowed.
/// </remarks>
internal static class ErasureRetry
{
    public const int Attempts = 3;

    public static async Task RunAsync(IDocumentSession session, Func<Task> attempt)
    {
        for (var tried = 1; ; tried++)
        {
            try
            {
                await attempt();
                return;
            }
            catch (JasperFx.ConcurrencyException) when (tried < Attempts)
            {
                session.EjectAllPendingChanges();
            }
        }
    }
}

using barakoCMS.Core.Interfaces;
using Marten;

namespace barakoCMS.Infrastructure.Envelope;

/// <summary>Puts the ambient branch on every event a session is about to store that has none.</summary>
/// <remarks>
/// Only the branch. The origin sequence is reserved by <see cref="Services.ContentWriter"/> before
/// it stages the events, because the gapless counter needs a query on the session, and a query from
/// inside a before-save listener is not something Marten promises to allow (core plan, Task 1).
/// An event that already carries a branch, as a module's own events do, keeps it.
/// </remarks>
internal sealed class EventEnvelopeListener : DocumentSessionListenerBase
{
    public override Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
    {
        if (EventEnvelopeContext.CurrentBranch is not { } branch) return Task.CompletedTask;

        var value = branch.ToString();
        foreach (var stream in session.PendingChanges.Streams())
        {
            foreach (var @event in stream.Events)
            {
                if (@event.GetHeader(EnvelopeHeaders.BranchId) is null)
                    @event.SetHeader(EnvelopeHeaders.BranchId, value);
            }
        }

        return Task.CompletedTask;
    }
}

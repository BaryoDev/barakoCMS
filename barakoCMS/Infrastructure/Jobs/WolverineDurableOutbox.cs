using barakoCMS.Core.Interfaces;
using Marten;
using Wolverine.Marten;

namespace barakoCMS.Infrastructure.Jobs;

/// <summary>
/// <see cref="IDurableOutbox"/> on Wolverine's Marten outbox. A message is staged in the scoped
/// session the caller is writing with, stored in the same commit as that session's documents, and
/// handled by <see cref="DurableMessageDispatcher"/> in the tenant of that session. A caller that
/// throws before saving leaves no envelope.
/// </summary>
internal sealed class WolverineDurableOutbox(IMartenOutbox outbox, IDocumentSession session) : IDurableOutbox
{
    private bool _enrolled;

    public Task<string> EnqueueAsync<TMessage>(TMessage message, CancellationToken cancellationToken)
        where TMessage : class =>
        StageAsync(message, null);

    public Task<string> ScheduleAsync<TMessage>(TMessage message, DateTimeOffset dueAt, CancellationToken cancellationToken)
        where TMessage : class =>
        StageAsync(message, dueAt);

    private async Task<string> StageAsync<TMessage>(TMessage message, DateTimeOffset? dueAt)
        where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!_enrolled)
        {
            outbox.Enroll(session);
            _enrolled = true;
        }

        var wrapped = DurableMessage.Wrap(message);
        await outbox.PublishAsync(wrapped, DurableMessage.Options(session.TenantId, dueAt));
        return wrapped.MessageId;
    }
}

/// <summary>
/// Queues a message for a tenant named on the call, for a caller that has no request and so no
/// session of its own: a <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>. Opens a
/// session for that tenant, stages the message in it and saves, which commits the envelope.
/// </summary>
public sealed class DurableTenantPublisher(
    IServiceScopeFactory scopes,
    DurableTenantSessionFactory sessions)
{
    /// <returns>The message's id, the <see cref="DurableMessageContext.MessageId"/> its handler is given.</returns>
    public async Task<string> EnqueueAsync<TMessage>(string tenant, TMessage message, CancellationToken cancellationToken)
        where TMessage : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentNullException.ThrowIfNull(message);

        await using var session = sessions.Open(tenant);
        using var scope = scopes.CreateScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IMartenOutbox>();
        outbox.Enroll(session);

        var wrapped = DurableMessage.Wrap(message);
        await outbox.PublishAsync(wrapped, DurableMessage.Options(session.TenantId, null));
        await session.SaveChangesAsync(cancellationToken);
        return wrapped.MessageId;
    }
}

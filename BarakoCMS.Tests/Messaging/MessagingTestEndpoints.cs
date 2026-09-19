using barakoCMS.Infrastructure.Messaging;
using FastEndpoints;
using JasperFx;
using Marten;
using Wolverine;
using Wolverine.Marten;

namespace BarakoCMS.Tests.Messaging;

/// <summary>
/// The Wolverine twins of <c>Jobs/JobTestEndpoints.cs</c>: a FastEndpoints endpoint publishes
/// through the outbox enrolled in the request's own Marten session, so the transactional property
/// of an enqueue can be exercised from inside a real request. Test host only.
/// </summary>
internal static class Outbox
{
    /// <summary>
    /// The outbox does not stamp the session's tenant on the envelope, so a request in a tenant
    /// says so explicitly. That is the one line WorkflowRunner would carry too.
    /// </summary>
    public static DeliveryOptions? TenantOf(IDocumentSession session) =>
        session.TenantId == StorageConstants.DefaultTenantId
            ? null
            : new DeliveryOptions { TenantId = session.TenantId };
}

internal sealed class PublishThenCommitEndpoint(IDocumentSession session, IMartenOutbox outbox) : EndpointWithoutRequest<string>
{
    public override void Configure()
    {
        Post("/api/_test/messages/publish-then-commit");
        AllowAnonymous();
        Description(b => b.ExcludeFromDescription());
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var marker = Query<string>("message")!;
        await outbox.PublishAsync(new LogMessage(marker), Outbox.TenantOf(session));
        await session.SaveChangesAsync(ct);
        await Send.OkAsync(marker, ct);
    }
}

internal sealed class PublishThenThrowEndpoint(IDocumentSession session, IMartenOutbox outbox) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/api/_test/messages/publish-then-throw");
        AllowAnonymous();
        Description(b => b.ExcludeFromDescription());
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await outbox.PublishAsync(new LogMessage(Query<string>("message")!), Outbox.TenantOf(session));
        throw new InvalidOperationException("Thrown after the message was published and before the session committed.");
    }
}

/// <summary>
/// Publishes, then stores a role whose name already exists, so the commit itself fails on the
/// unique index: the publish succeeded, the write it belongs to did not.
/// </summary>
internal sealed class PublishThenFailCommitEndpoint(IDocumentSession session, IMartenOutbox outbox) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/api/_test/messages/publish-then-fail-commit");
        AllowAnonymous();
        Description(b => b.ExcludeFromDescription());
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await outbox.PublishAsync(new LogMessage(Query<string>("message")!), Outbox.TenantOf(session));
        session.Store(new barakoCMS.Models.Role { Id = Guid.NewGuid(), Name = "SuperAdmin" });
        await session.SaveChangesAsync(ct);
    }
}

internal sealed class PublishFailingEndpoint(IDocumentSession session, IMartenOutbox outbox) : EndpointWithoutRequest<string>
{
    public override void Configure()
    {
        Post("/api/_test/messages/publish-failing");
        AllowAnonymous();
        Description(b => b.ExcludeFromDescription());
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var marker = Query<string>("message")!;
        await outbox.PublishAsync(new AlwaysFails(marker), Outbox.TenantOf(session));
        await session.SaveChangesAsync(ct);
        await Send.OkAsync(marker, ct);
    }
}

/// <summary>Publishes one webhook delivery for an entry, the way a runner would.</summary>
internal sealed class PublishWebhookEndpoint(IDocumentSession session, IMartenOutbox outbox) : EndpointWithoutRequest<string>
{
    public override void Configure()
    {
        Post("/api/_test/messages/publish-webhook");
        AllowAnonymous();
        Description(b => b.ExcludeFromDescription());
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var key = Query<string>("key")!;
        var parameters = new Dictionary<string, string>
        {
            ["Url"] = Query<string>("url")!,
            ["IdempotencyKey"] = key,
            ["TriggerEvent"] = "Published",
            ["Attempt"] = "1",
        };

        await outbox.PublishAsync(new DeliverWebhook(Query<Guid>("contentId"), parameters), Outbox.TenantOf(session));
        await session.SaveChangesAsync(ct);
        await Send.OkAsync(key, ct);
    }
}

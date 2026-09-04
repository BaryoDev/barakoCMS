using barakoCMS.Infrastructure.Jobs;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Tests.Jobs;

/// <summary>
/// Endpoints that exist only in the test host, so the transactional property of an enqueue can be
/// exercised from inside a real request. They are anonymous and kept out of the OpenAPI document;
/// FastEndpoints scans this assembly because it is loaded, and never in a shipped host.
/// </summary>
internal sealed class EnqueueThenThrowEndpoint(IDocumentSession session) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/api/_test/jobs/enqueue-then-throw");
        AllowAnonymous();
        Description(b => b.ExcludeFromDescription());
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        _ = session;
        await new LogMessageCommand { Message = Query<string>("message")! }.QueueJobAsync(ct: ct);
        throw new InvalidOperationException("Thrown after the job was queued and before the session committed.");
    }
}

/// <summary>
/// Queues, then stores a role whose name already exists, so the commit itself fails on the unique
/// index. This is the case the issue names: the store succeeded, the write it belongs to did not.
/// </summary>
internal sealed class EnqueueThenFailCommitEndpoint(IDocumentSession session) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/api/_test/jobs/enqueue-then-fail-commit");
        AllowAnonymous();
        Description(b => b.ExcludeFromDescription());
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await new LogMessageCommand { Message = Query<string>("message")! }.QueueJobAsync(ct: ct);
        session.Store(new barakoCMS.Models.Role { Id = Guid.NewGuid(), Name = "SuperAdmin" });
        await session.SaveChangesAsync(ct);
    }
}

internal sealed class EnqueueThenCommitEndpoint(IDocumentSession session) : EndpointWithoutRequest<Guid>
{
    public override void Configure()
    {
        Post("/api/_test/jobs/enqueue-then-commit");
        AllowAnonymous();
        Description(b => b.ExcludeFromDescription());
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var id = await new LogMessageCommand { Message = Query<string>("message")! }.QueueJobAsync(ct: ct);
        await session.SaveChangesAsync(ct);
        await Send.OkAsync(id, ct);
    }
}

internal sealed class EnqueueFailingJobEndpoint(IDocumentSession session) : EndpointWithoutRequest<Guid>
{
    public override void Configure()
    {
        Post("/api/_test/jobs/enqueue-failing");
        AllowAnonymous();
        Description(b => b.ExcludeFromDescription());
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var id = await new AlwaysFailsCommand { Marker = Query<string>("message")! }.QueueJobAsync(ct: ct);
        await session.SaveChangesAsync(ct);
        await Send.OkAsync(id, ct);
    }
}

internal sealed class AlwaysFailsCommand : ICommand
{
    public string Marker { get; set; } = string.Empty;
}

internal sealed class AlwaysFailsCommandHandler : ICommandHandler<AlwaysFailsCommand>
{
    public const string Reason = "This handler fails on purpose.";

    public Task ExecuteAsync(AlwaysFailsCommand command, CancellationToken ct) =>
        throw new InvalidOperationException(Reason);
}

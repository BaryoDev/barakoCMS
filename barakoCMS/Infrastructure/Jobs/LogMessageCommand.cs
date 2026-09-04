using FastEndpoints;

namespace barakoCMS.Infrastructure.Jobs;

/// <summary>
/// The one command the queue ships with: it writes its message to the log. It exists so the queue
/// can be proven to run something without pulling email or indexing onto it yet.
/// </summary>
internal sealed class LogMessageCommand : ICommand
{
    public string Message { get; set; } = string.Empty;
}

/// <remarks>
/// A handler runs on a worker with no request, so FastEndpoints builds it from the root container.
/// Take singletons, or <c>IDocumentStore</c> and open a session for the tenant the command names.
/// Asking for a scoped <c>IDocumentSession</c> here fails at construction.
/// </remarks>
internal sealed class LogMessageCommandHandler(ILogger<LogMessageCommandHandler> logger)
    : ICommandHandler<LogMessageCommand>
{
    public Task ExecuteAsync(LogMessageCommand command, CancellationToken ct)
    {
        logger.LogInformation("Queued job says: {Message}", command.Message);
        return Task.CompletedTask;
    }
}

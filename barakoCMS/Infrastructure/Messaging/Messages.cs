using System.Collections.Concurrent;
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Multitenancy;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Marten;

namespace barakoCMS.Infrastructure.Messaging;

/// <summary>
/// What every handler in this spike reports back, so a test can see a message was handled, how
/// many times, and in which tenant, without a table of its own.
/// </summary>
public sealed class MessageRecorder
{
    private readonly ConcurrentQueue<string> _entries = new();

    public void Record(string entry) => _entries.Enqueue(entry);

    public bool Has(string entry) => _entries.Contains(entry);

    public int Count(string prefix) => _entries.Count(e => e.StartsWith(prefix, StringComparison.Ordinal));
}

/// <summary>The message twin of <see cref="Jobs.LogMessageCommand"/>.</summary>
public sealed record LogMessage(string Text);

/// <summary>
/// Public, and so are the other handlers below. Wolverine's conventional discovery skips
/// non-public types, and an explicitly included internal type is still referenced from generated
/// code that is compiled into another assembly. That is the first concrete cost to section 6 that
/// this spike found (#687, question 6).
/// </summary>
public static class LogMessageHandler
{
    public static void Handle(LogMessage message, TenantId tenantId, MessageRecorder recorder, ILogger<LogMessage> logger)
    {
        recorder.Record($"{message.Text}@{TenantScopes.SlugFor(tenantId.Value)}");
        logger.LogInformation("Handled a log message in tenant {Tenant}", TenantScopes.SlugFor(tenantId.Value));
    }
}

public sealed record AlwaysFails(string Marker);

public static class AlwaysFailsHandler
{
    public const string Reason = "This handler fails on purpose.";

    public static void Handle(AlwaysFails message, MessageRecorder recorder)
    {
        recorder.Record($"attempt:{message.Marker}");
        throw new InvalidOperationException(Reason);
    }
}

/// <summary>
/// One webhook delivery, carried the way WorkflowRunner hands it to WebhookAction: the parameters
/// already resolved, with the idempotency key and the run's identifiers inside them.
/// </summary>
public sealed record DeliverWebhook(Guid ContentId, Dictionary<string, string> Parameters);

/// <summary>Thrown so Wolverine's retry policy sees a retryable failure as a failure.</summary>
public sealed class WebhookDeliveryFailedException(string? reason) : Exception(reason ?? "The webhook was not delivered.");

public static class DeliverWebhookHandler
{
    /// <remarks>
    /// The tenant rides the envelope and arrives as <paramref name="tenantId"/>. The action is
    /// resolved from a scope bound to that tenant, exactly as <c>WorkflowRunner.ExecuteAsync</c>
    /// does it, so the delivery row WebhookAction writes lands in the tenant's partition. A
    /// permanent failure is recorded and not thrown: retrying a refused URL five times is what the
    /// runner's <c>Retryable</c> flag exists to prevent.
    /// </remarks>
    public static async Task Handle(
        DeliverWebhook message, TenantId tenantId, IServiceScopeFactory scopes, MessageRecorder recorder, CancellationToken ct)
    {
        var key = message.Parameters.GetValueOrDefault("IdempotencyKey") ?? message.ContentId.ToString();

        // IServiceScopeFactory rather than IServiceProvider: Wolverine refuses to generate a handler
        // that service-locates (ServiceLocationPolicy.NotAllowed is its default), and a scope factory
        // is an ordinary singleton dependency.
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Slug = TenantScopes.SlugFor(tenantId.Value);
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var content = await session.LoadAsync<barakoCMS.Models.Content>(message.ContentId, ct);

        if (content is null)
        {
            recorder.Record($"webhook:{key}:skipped");
            return;
        }

        var action = scope.ServiceProvider.GetServices<IWorkflowAction>().First(a => a.Type == "Webhook");
        var result = await action.RunAsync(new Dictionary<string, string>(message.Parameters), content, ct);

        if (result.Succeeded)
        {
            recorder.Record($"webhook:{key}:ok");
            return;
        }

        recorder.Record($"webhook:{key}:{(result.Retryable ? "failed" : "permanent")}");

        if (result.Retryable)
        {
            throw new WebhookDeliveryFailedException(result.Error);
        }
    }
}

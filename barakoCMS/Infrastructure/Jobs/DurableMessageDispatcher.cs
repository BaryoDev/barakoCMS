using System.Reflection;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Multitenancy;
using Wolverine;
using Wolverine.Attributes;

namespace barakoCMS.Infrastructure.Jobs;

/// <summary>
/// Wolverine's one handler. It reads the stored message back, builds a scope bound to the
/// envelope's tenant and gives the message to the <see cref="IDurableMessageHandler{TMessage}"/>
/// registered for its type. Public because Wolverine discovers only public handlers, and it takes a
/// scope factory because Wolverine refuses a handler that takes the service provider.
/// </summary>
[WolverineHandler]
public sealed class DurableMessageDispatcher
{
    public async Task Handle(
        DurableMessage message,
        Envelope envelope,
        IServiceScopeFactory scopes,
        CancellationToken cancellationToken)
    {
        var type = Type.GetType(message.TypeName)
            ?? throw new InvalidOperationException("No type is known for a queued durable message.");
        var payload = JsonSerializer.Deserialize(message.Json, type)
            ?? throw new InvalidOperationException("A queued durable message had no body.");

        var slug = TenantScopes.SlugFor(envelope.TenantId);
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Slug = slug;

        var handlerType = typeof(IDurableMessageHandler<>).MakeGenericType(type);
        var handler = scope.ServiceProvider.GetService(handlerType)
            ?? throw new InvalidOperationException(
                $"No handler is registered for the durable message type {type.Name}.");

        var context = new DurableMessageContext { Tenant = slug, MessageId = message.MessageId };
        var call = (Task)handlerType
            .GetMethod(nameof(IDurableMessageHandler<object>.HandleAsync))!
            .Invoke(handler, [payload, context, cancellationToken])!;
        await call;
    }
}

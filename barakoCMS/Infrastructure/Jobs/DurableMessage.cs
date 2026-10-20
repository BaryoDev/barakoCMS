using System.Text.Json;
using barakoCMS.Core.Interfaces;
using Wolverine;

namespace barakoCMS.Infrastructure.Jobs;

/// <summary>
/// What Wolverine stores and moves for every message queued through <see cref="IDurableOutbox"/>.
/// Public because Wolverine routes and handles only public types.
/// </summary>
/// <param name="MessageId">The id the caller was given back, the same on every delivery.</param>
/// <param name="TypeName">The assembly qualified name of the queued message's type.</param>
/// <param name="Json">The queued message, as JSON.</param>
public sealed record DurableMessage(string MessageId, string TypeName, string Json)
{
    internal static DurableMessage Wrap<TMessage>(TMessage message)
        where TMessage : class =>
        new(
            Guid.NewGuid().ToString("N"),
            message.GetType().AssemblyQualifiedName!,
            JsonSerializer.Serialize(message, message.GetType()));

    internal static DeliveryOptions Options(string? martenTenantId, DateTimeOffset? dueAt)
    {
        var options = new DeliveryOptions();
        if (!string.IsNullOrEmpty(martenTenantId))
            options.TenantId = martenTenantId;
        if (dueAt is { } at)
            options.ScheduledTime = at;
        return options;
    }
}

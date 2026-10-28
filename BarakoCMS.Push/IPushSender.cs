namespace BarakoCMS.Push;

public sealed record PushSendResult(int Attempted, int Delivered, int Pruned, int Failed, bool Disabled);

public interface IPushSender
{
    Task<PushSendResult> SendToUsersAsync(string? tenant, IReadOnlyCollection<Guid> userIds, PushNotification n, CancellationToken ct);
    Task<PushSendResult> SendToTenantAsync(string? tenant, PushNotification n, CancellationToken ct);
}

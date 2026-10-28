namespace BarakoCMS.Push;

public interface IPushTransport
{
    /// <summary>Returns the HTTP status the push service answered. 201 means accepted.</summary>
    Task<int> SendAsync(StoredSubscription subscription, string payloadJson, CancellationToken ct);
}

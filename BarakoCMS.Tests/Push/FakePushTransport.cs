using BarakoCMS.Push;

namespace BarakoCMS.Tests.Push;

public sealed class FakePushTransport : IPushTransport
{
    private readonly object _gate = new();
    private readonly List<(Guid SubscriptionId, string Payload)> _sent = new();

    public Func<StoredSubscription, int> Respond { get; set; } = _ => 201;

    public IReadOnlyList<(Guid SubscriptionId, string Payload)> Sent
    {
        get { lock (_gate) return _sent.ToList(); }
    }

    public void Reset(Func<StoredSubscription, int> respond)
    {
        lock (_gate) _sent.Clear();
        Respond = respond;
    }

    public void Clear()
    {
        lock (_gate) _sent.Clear();
    }

    public Task<int> SendAsync(StoredSubscription subscription, string payloadJson, CancellationToken ct)
    {
        var status = Respond(subscription);
        lock (_gate) _sent.Add((subscription.Id, payloadJson));
        return Task.FromResult(status);
    }
}

using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;

namespace BarakoCMS.Push;

public sealed class WebPushTransport : IPushTransport
{
    private readonly PushServiceClient _client;

    public WebPushTransport(HttpClient http, PushOptions options)
    {
        _client = new PushServiceClient(http)
        {
            AutoRetryAfter = false,
            DefaultAuthentication = new VapidAuthentication(options.PublicKey!, options.PrivateKey!)
            {
                Subject = options.Subject!,
            },
        };
    }

    public async Task<int> SendAsync(StoredSubscription s, string payloadJson, CancellationToken ct)
    {
        var sub = new PushSubscription { Endpoint = s.Endpoint };
        sub.SetKey(PushEncryptionKeyName.P256DH, s.P256dh);
        sub.SetKey(PushEncryptionKeyName.Auth, s.Auth);
        var message = new PushMessage(payloadJson) { Urgency = PushMessageUrgency.Normal, TimeToLive = 86400 };
        try
        {
            await _client.RequestPushMessageDeliveryAsync(sub, message, ct);
            return 201;
        }
        catch (PushServiceClientException ex)
        {
            return (int)ex.StatusCode;
        }
    }
}

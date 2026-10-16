using FastEndpoints;

namespace BarakoCMS.Push.Features;

public sealed class ConfigEndpoint(PushOptions options) : EndpointWithoutRequest
{
    public override void Configure() => Get("/api/push/config");

    public override Task HandleAsync(CancellationToken ct) =>
        Send.OkAsync(new { enabled = options.IsConfigured, publicKey = options.PublicKey }, ct);
}

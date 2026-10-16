using barakoCMS.Infrastructure.Multitenancy;
using FastEndpoints;

namespace BarakoCMS.Push.Features;

public sealed class TestEndpoint(IPushSender sender, TenantContext tenant) : EndpointWithoutRequest
{
    public override void Configure() => Post("/api/push/test");

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var n = new PushNotification("Test notification", "Push is working on this device.", "/");
        var result = await sender.SendToUsersAsync(tenant.Slug, [userId], n, ct);
        await Send.OkAsync(result, ct);
    }
}

using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using FastEndpoints;
using Microsoft.AspNetCore.Http;

namespace BarakoCMS.Push.Features;

public sealed class SendRequest
{
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? Url { get; set; }
    public List<Guid>? UserIds { get; set; }
}

public sealed class SendEndpoint(IPushSender sender, TenantContext tenant) : Endpoint<SendRequest>
{
    public override void Configure()
    {
        Post("/api/push/send");
        Definition.RequireCapability(PushCapabilities.SendPush, PushCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(SendRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Body))
        {
            await Send.ResultAsync(Results.BadRequest("Title and Body are required."));
            return;
        }

        PushNotification n = new(req.Title, req.Body, PushNotification.SafeUrl(req.Url));
        try { n.ToJson(); }
        catch (ArgumentException)
        {
            await Send.ResultAsync(Results.BadRequest("Message is too long."));
            return;
        }

        var result = req.UserIds is { Count: > 0 }
            ? await sender.SendToUsersAsync(tenant.Slug, req.UserIds, n, ct)
            : await sender.SendToTenantAsync(tenant.Slug, n, ct);
        await Send.OkAsync(result, ct);
    }
}

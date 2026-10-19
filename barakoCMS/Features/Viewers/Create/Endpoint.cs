using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Viewers;
using barakoCMS.Models;
using barakoCMS.Infrastructure.Audit;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Viewers.Create;

/// <summary>
/// POST /api/viewer-tokens: a site with its own sign-in mints a token for one of its members.
/// </summary>
/// <remarks>
/// Gated like the other delivery decisions. The token is for the Members audience of public
/// delivery only: it is signed under its own audience, so no authoring route accepts it.
/// </remarks>
[barakoCMS.Infrastructure.Filters.NoIdempotentReplay]
internal sealed class Endpoint(
    IDocumentSession session,
    IConfiguration config,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    : Endpoint<CreateViewerTokenRequest, CreateViewerTokenResponse>
{
    public override void Configure()
    {
        Post("/api/viewer-tokens");
        Definition.RequireCapability(SystemCapabilities.ManagePublicDelivery, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CreateViewerTokenRequest req, CancellationToken ct)
    {
        if (!ViewerToken.IsConfigured(config))
        {
            AddError("Viewer tokens are signed with JWT:Key, which is not set on this server.");
            await Send.ErrorsAsync(StatusCodes.Status503ServiceUnavailable, ct);
            return;
        }

        var lifetime = req.LifetimeMinutes is { } m ? TimeSpan.FromMinutes(m) : (TimeSpan?)null;
        var subject = req.Subject.Trim();
        var (token, expiresAt) = ViewerToken.Create(config, tenant.Slug, subject, lifetime);
        var actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var parsed) ? parsed : (Guid?)null;

        // The token itself is never written down, only who it was minted for and until when.
        await AuditLog.RecordAsync(
            session,
            tenant.Slug,
            "viewer.token.minted",
            actorId,
            User.FindFirst("Username")?.Value,
            targetType: "Viewer",
            targetId: subject,
            metadata: new Dictionary<string, object>
            {
                ["subject"] = subject,
                ["tenant"] = tenant.Slug,
                ["expiresAt"] = expiresAt.ToString("O"),
            },
            ct: ct);
        await session.SaveChangesAsync(ct);

        await Send.ResponseAsync(new CreateViewerTokenResponse { Token = token, ExpiresAt = expiresAt }, cancellation: ct);
    }
}

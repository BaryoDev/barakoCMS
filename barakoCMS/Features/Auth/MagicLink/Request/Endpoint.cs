using System.Net;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Auth.MagicLink.Request;

/// <summary>
/// POST /api/auth/magic-link/request: email a single-use sign-in link to a registered user.
/// Always answers 200 with the same message so callers cannot probe which emails exist.
/// </summary>
/// <remarks>
/// Gated by the same tenant sign-in method as codes (<c>otp</c>), since both prove the mailbox.
/// Rate limited by the same "auth" policy as the code request.
/// </remarks>
internal class Endpoint(
    IDocumentSession session,
    AuthMailQueue mailQueue,
    IConfiguration config,
    TenantContext tenant,
    ILogger<Endpoint> logger) : Endpoint<MagicLinkRequest, MagicLinkRequestResponse>
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    internal const string ConfirmPath = "/api/auth/magic-link/confirm";

    public override void Configure()
    {
        Post("/api/auth/magic-link/request");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting("auth"));
    }

    public override async Task HandleAsync(MagicLinkRequest req, CancellationToken ct)
    {
        if (!await TenantSignIn.AllowsAsync(session, tenant.Slug, TenantSignInPolicy.Otp, ct))
        {
            ThrowError(TenantSignIn.RefusedMessage(TenantSignInPolicy.Otp), 403);
            return;
        }

        var address = (req.Email ?? string.Empty).Trim().ToLowerInvariant();
        var ok = new MagicLinkRequestResponse { Message = "If that email is registered, a sign-in link has been sent." };

        if (!string.IsNullOrWhiteSpace(address) && address.Contains('@'))
        {
            var exists = await session.Query<User>().AnyAsync(u => u.NormalizedEmail == address, ct);
            if (exists)
            {
                // The outcome is not reflected: the response must be identical for every address.
                _ = await IssueAndQueueAsync(address, ct);
            }
            else
            {
                // Comparable work for an address with no account: the lookup of outstanding links
                // and the token. The row write is the one step it skips. Neither branch sends inline.
                _ = await session.Query<MagicLinkToken>().Where(t => t.Email == address && !t.Consumed).ToListAsync(ct);
                _ = EmailVerificationToken.Create(Guid.NewGuid());
            }
        }

        await Send.ResponseAsync(ok, cancellation: ct);
    }

    private async Task<bool> IssueAndQueueAsync(string address, CancellationToken ct)
    {
        var outstanding = await session.Query<MagicLinkToken>()
            .Where(t => t.Email == address && !t.Consumed)
            .ToListAsync(ct);
        foreach (var stale in outstanding)
        {
            stale.Consumed = true;
            session.Update(stale);
        }

        var row = new MagicLinkToken
        {
            Email = address,
            TenantSlug = tenant.Slug,
            ExpiresAt = DateTime.UtcNow.Add(Lifetime),
        };
        var (token, hash) = EmailVerificationToken.Create(row.Id);
        row.TokenHash = hash;
        session.Store(row);

        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (JasperFx.ConcurrencyException)
        {
            logger.LogWarning("Concurrent magic link request for the same address; no link was issued");
            return false;
        }

        var appName = config["Branding:AppName"] ?? "BarakoCMS";
        var body =
            $"<p>Use this link to sign in to {WebUtility.HtmlEncode(appName)}:</p>"
          + Link(token)
          + $"<p>It expires in {(int)Lifetime.TotalMinutes} minutes and works once. "
          + "If this wasn't you, ignore this email and nothing happens.</p>";
        mailQueue.Enqueue(address, $"Your {appName} sign-in link", body, "magic-link");
        return true;
    }

    /// <summary>
    /// A clickable link where the deployment has set its public URL, the bare token where it has
    /// not. The host header is never used, for the reason <see cref="CanonicalHost"/> gives.
    /// </summary>
    private string Link(string token)
    {
        var baseUrl = config[CanonicalHost.BaseUrlKey]?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
        {
            return $"<p style=\"font-family:monospace;word-break:break-all\">{WebUtility.HtmlEncode(token)}</p>";
        }

        var link = WebUtility.HtmlEncode($"{baseUrl}{ConfirmPath}?token={Uri.EscapeDataString(token)}");
        return $"<p><a href=\"{link}\">Sign in</a></p>"
             + $"<p style=\"font-family:monospace;word-break:break-all\">{link}</p>";
    }
}

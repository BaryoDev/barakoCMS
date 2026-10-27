using FastEndpoints;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Settings.Email;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Auth.Methods;

/// <summary>
/// GET /api/auth/methods: the ids of the ways in this instance offers, so a client draws its
/// sign-in choices from the server. Ids only. Labels belong to the client.
/// </summary>
/// <remarks>
/// <c>password</c> is listed unless the tenant's policy excludes it. <c>email-code</c> needs the
/// tenant to allow <c>otp</c> and an email provider that delivers (not the mock). SMTP delivers with
/// only a host, so an API key is not the test. Any other id comes from an <see cref="ISignInMethod"/>
/// a module registers. <c>/api/auth/providers</c> still lists social providers.
/// </remarks>
internal class Endpoint(
    IQuerySession session,
    TenantContext tenant,
    IEmailService email,
    IEnumerable<ISignInMethod> moduleMethods) : EndpointWithoutRequest<Response>
{
    public const string PasswordId = "password";
    public const string EmailCodeId = "email-code";

    public override void Configure()
    {
        Get("/api/auth/methods");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting(RateLimitSetup.DeliveryPolicy));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var slug = string.IsNullOrWhiteSpace(tenant.Slug) ? Tenant.DefaultSlug : tenant.Slug.Trim().ToLowerInvariant();
        var row = slug == Tenant.DefaultSlug
            ? null
            : await session.Query<Tenant>().FirstOrDefaultAsync(t => t.Slug == slug, ct);

        // The default tenant and a slug nobody registered allow every built-in method, as sign-in does.
        var policy = row?.SignIn;
        var methods = new List<string>();

        if (policy is null || policy.Allows(TenantSignInPolicy.Password))
        {
            methods.Add(PasswordId);
        }

        if ((policy is null || policy.Allows(TenantSignInPolicy.Otp)) && !EmailProvider.IsMock(email))
        {
            methods.Add(EmailCodeId);
        }

        var reserved = TenantSignInPolicy.KnownMethods.Append(EmailCodeId).ToList();
        var ids = moduleMethods
            .Select(m => m.Name.Trim().ToLowerInvariant())
            .Where(n => n.Length > 0 && !reserved.Contains(n))
            .Distinct();

        foreach (var id in ids)
        {
            // Off until a registered, active tenant names it. The default tenant names nothing, so
            // it lists what is installed.
            if (row is null || (row.Status == TenantStatus.Active && policy!.AllowsExplicitly(id)))
            {
                methods.Add(id);
            }
        }

        await Send.OkAsync(new Response { Methods = methods }, ct);
    }
}

using FastEndpoints;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Auth.Methods;

/// <summary>
/// GET /api/auth/methods: the ids of the ways in this instance offers, so a client draws its
/// sign-in choices from the server. Ids only. Labels belong to the client.
/// </summary>
/// <remarks>
/// <c>password</c> is always a method unless the tenant's policy excludes it. <c>email-code</c> needs
/// the tenant to allow <c>otp</c> and an email transport to be configured, the same two things a
/// sign-in code needs to arrive. Any other id comes from an <see cref="ISignInMethod"/> a module
/// registers. <c>/api/auth/providers</c> still lists social providers.
/// </remarks>
internal class Endpoint(
    IQuerySession session,
    TenantContext tenant,
    IEmailSettingsProvider email,
    IEnumerable<ISignInMethod> moduleMethods) : EndpointWithoutRequest<Response>
{
    public const string PasswordId = "password";
    public const string EmailCodeId = "email-code";

    public override void Configure()
    {
        Get("/api/auth/methods");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var methods = new List<string>();

        if (await TenantSignIn.AllowsAsync(session, tenant.Slug, TenantSignInPolicy.Password, ct))
        {
            methods.Add(PasswordId);
        }

        if (await TenantSignIn.AllowsAsync(session, tenant.Slug, TenantSignInPolicy.Otp, ct)
            && !string.IsNullOrWhiteSpace((await email.GetForTenantAsync(tenant.Slug, ct)).ApiKey))
        {
            methods.Add(EmailCodeId);
        }

        foreach (var id in moduleMethods.Select(m => m.Name.Trim().ToLowerInvariant()).Where(n => n.Length > 0).Distinct())
        {
            if (methods.Contains(id) || id == PasswordId || id == EmailCodeId)
            {
                continue;
            }

            // The default tenant names nothing, so a module method is listed there as installed.
            // Any other tenant lists it only when its policy names it.
            if (tenant.IsDefault || await TenantSignIn.AllowsExplicitlyAsync(session, tenant.Slug, id, ct))
            {
                methods.Add(id);
            }
        }

        await Send.OkAsync(new Response { Methods = methods }, ct);
    }
}

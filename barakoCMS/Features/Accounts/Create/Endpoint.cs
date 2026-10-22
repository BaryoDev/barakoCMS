using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Accounts.Create;

/// <summary>POST /api/accounts: create an account (platform manager only).</summary>
internal class Endpoint(IDocumentSession session) : Endpoint<AccountWriteRequest, AccountResponse>
{
    public override void Configure()
    {
        Post("/api/accounts");
        Definition.RequireCapability(SystemCapabilities.ManageTenants, "SuperAdmin");
    }

    public override async Task HandleAsync(AccountWriteRequest req, CancellationToken ct)
    {
        var reach = await AccountReach.ForAsync(HttpContext, session, ct);
        if (reach is not { IsGlobal: true }) { await Send.ForbiddenAsync(ct); return; }

        var slug = req.Slug?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!TenantHandles.IsValidHandle(slug) || slug == "branding")
        { AddError(r => r.Slug, "Invalid or reserved slug (3-40 chars, a-z, 0-9, hyphens)."); ThrowIfAnyErrors(); }

        if (await session.Query<TenantAccount>().AnyAsync(a => a.Slug == slug, ct))
        { AddError(r => r.Slug, "An account with this slug already exists."); ThrowIfAnyErrors(); }

        var account = new TenantAccount
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            Name = req.Name.Trim(),
            LogoUrl = string.IsNullOrWhiteSpace(req.LogoUrl) ? null : req.LogoUrl.Trim(),
            Colours = req.Colours ?? new(),
        };
        session.Store(account);
        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            AddError(r => r.Slug, "An account with this slug already exists.");
            ThrowIfAnyErrors();
        }
        await Send.OkAsync(AccountResponse.From(account), ct);
    }

    private static bool IsUniqueViolation(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
            if (current is Npgsql.PostgresException { SqlState: "23505" }) return true;
        return false;
    }
}

using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation;
using Marten;

namespace BarakoCMS.ExternalAuth;

/// <summary>
/// The external providers one tenant lets its people sign in with. A tenant with no row uses the
/// deployment's providers as configured, which is what every tenant did before this existed.
/// </summary>
/// <remarks>
/// Global, like <see cref="ExternalRoleMap"/>, and keyed by the tenant's slug. This only narrows
/// what the deployment has configured: a provider named here still needs its deployment-level
/// client id and secret, which stay one set per deployment.
/// </remarks>
public class ExternalProviderSettings
{
    /// <summary>The tenant slug, lowercased.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The provider keys the tenant allows. Empty allows none. See <see cref="ExternalProviders"/>
    /// for what a key looks like.
    /// </summary>
    public List<string> Providers { get; set; } = [];

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Provider keys, and the per-tenant check every start and callback asks.</summary>
/// <remarks>
/// The four OAuth providers are named <c>oauth:google</c> and so on. The colon is what keeps them
/// from colliding with an OpenID Connect provider, whose name is lowercase letters, digits and
/// hyphens. That provider is keyed <c>oidc:{name}</c>. The same string is the provider and the
/// issuer of a sign-in through the OAuth provider, and an OpenID Connect issuer is always an https
/// URL, so an identity link made through one can never be matched by the other.
/// </remarks>
public static class ExternalProviders
{
    public const string Google = "oauth:google";
    public const string Facebook = "oauth:facebook";
    public const string GitHub = "oauth:github";
    public const string LinkedIn = "oauth:linkedin";

    public static readonly IReadOnlyList<string> BuiltIn = [Google, Facebook, GitHub, LinkedIn];

    public static string OidcKey(string name) => $"oidc:{name}";

    private static string Slug(string? club) =>
        string.IsNullOrWhiteSpace(club) ? Tenant.DefaultSlug : club.Trim().ToLowerInvariant();

    /// <summary>
    /// The keys the tenant allows, or null when it has no list of its own, which means the
    /// deployment's providers. The default tenant never has one.
    /// </summary>
    public static async Task<IReadOnlyList<string>?> ListedAsync(IQuerySession session, string? club, CancellationToken ct)
    {
        var slug = Slug(club);
        if (slug == Tenant.DefaultSlug)
        {
            return null;
        }

        var settings = await session.LoadAsync<ExternalProviderSettings>(slug, ct);
        return settings?.Providers;
    }

    /// <summary>Whether the tenant lets <paramref name="key"/> start or finish a sign-in. True when it has no list.</summary>
    public static async Task<bool> AllowedAsync(IQuerySession session, string? club, string key, CancellationToken ct)
    {
        var listed = await ListedAsync(session, club, ct);
        return listed is null || listed.Contains(key, StringComparer.Ordinal);
    }
}

internal sealed record ExternalProvidersRequest(List<string> Providers);

internal sealed record ExternalProvidersResponse(List<string> Providers, DateTime UpdatedAt);

internal sealed class ExternalProvidersValidator : Validator<ExternalProvidersRequest>
{
    public const int MaxProviders = 50;

    public ExternalProvidersValidator(IConfiguration config)
    {
        RuleFor(x => x.Providers).NotNull()
            .Must(p => p is { Count: <= MaxProviders }).WithMessage($"A tenant lists at most {MaxProviders} providers.")
            .Must(p => p is null || p.Distinct(StringComparer.Ordinal).Count() == p.Count)
            .WithMessage("A provider is listed once.");
        RuleForEach(x => x.Providers)
            .Must(key => key is not null && (ExternalProviders.BuiltIn.Contains(key)
                || OidcProviders.Enabled(config).Any(p => ExternalProviders.OidcKey(p.Name) == key)))
            .WithMessage("A provider is oauth:google, oauth:facebook, oauth:github, oauth:linkedin, or oidc:{name} of a configured OpenID Connect provider.");
    }
}

/// <summary>GET /api/tenants/external-providers: the caller's tenant's provider list.</summary>
internal sealed class GetExternalProvidersEndpoint(IQuerySession session, TenantContext tenant)
    : EndpointWithoutRequest<ExternalProvidersResponse>
{
    public override void Configure()
    {
        Get("/api/tenants/external-providers");
        Definition.RequireCapability(SystemCapabilities.ManageTenantMembers, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var settings = await session.LoadAsync<ExternalProviderSettings>(tenant.Slug, ct);
        if (settings is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(new ExternalProvidersResponse(settings.Providers, settings.UpdatedAt), ct);
    }
}

/// <summary>PUT /api/tenants/external-providers: sets the caller's tenant's list, replacing any.</summary>
internal sealed class PutExternalProvidersEndpoint(IDocumentSession session, TenantContext tenant)
    : Endpoint<ExternalProvidersRequest, ExternalProvidersResponse>
{
    public override void Configure()
    {
        Put("/api/tenants/external-providers");
        Definition.RequireCapability(SystemCapabilities.ManageTenantMembers, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(ExternalProvidersRequest req, CancellationToken ct)
    {
        var slug = tenant.Slug;
        if (slug == Tenant.DefaultSlug)
        {
            AddError("The default tenant uses the deployment's providers.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var settings = new ExternalProviderSettings { Id = slug, Providers = req.Providers.ToList(), UpdatedAt = DateTime.UtcNow };
        session.Store(settings);
        await RecordAsync(session, User, "tenant.external_providers.updated", settings, ct);
        await session.SaveChangesAsync(ct);
        await Send.OkAsync(new ExternalProvidersResponse(settings.Providers, settings.UpdatedAt), ct);
    }

    internal static Task RecordAsync(
        IDocumentSession session, System.Security.Claims.ClaimsPrincipal actor, string action,
        ExternalProviderSettings settings, CancellationToken ct)
    {
        Guid.TryParse(actor.FindFirst("UserId")?.Value, out var actorId);
        var metadata = new Dictionary<string, object> { ["providers"] = settings.Providers.ToList() };
        return AuditLog.RecordAsync(session, settings.Id, action, actorId, actor.FindFirst("Username")?.Value,
            targetType: "ExternalProviderSettings", targetId: settings.Id, metadata: metadata, ct: ct);
    }
}

/// <summary>DELETE /api/tenants/external-providers: removes the list, so the tenant uses the deployment's providers.</summary>
internal sealed class DeleteExternalProvidersEndpoint(IDocumentSession session, TenantContext tenant)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/api/tenants/external-providers");
        Definition.RequireCapability(SystemCapabilities.ManageTenantMembers, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var settings = await session.LoadAsync<ExternalProviderSettings>(tenant.Slug, ct);
        if (settings is not null)
        {
            session.Delete(settings);
            await PutExternalProvidersEndpoint.RecordAsync(session, User, "tenant.external_providers.removed", settings, ct);
        }

        await session.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}

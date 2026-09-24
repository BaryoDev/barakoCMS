using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.ApiKeys;

// API keys are managed by a human admin (JWT) and scoped to the admin's current tenant. They act as
// the creating user, so a key can never do more than its creator, and its scopes narrow it further to
// the content surface. Naming content types makes it a push key, limited to
// POST /api/collections/{type}/push for those types. The secret is shown once, on create, and only
// its hash is stored.

internal sealed class CreateApiKeyRequest
{
    public string Name { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = new();
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Limits the key to pushing to these content types. Empty for no limit.</summary>
    public List<string>? ContentTypes { get; set; }
}

internal sealed record CreateApiKeyResponse(
    Guid Id, string Key, string Prefix, string Name, List<string> Scopes,
    string TenantSlug, DateTime? ExpiresAt, DateTime CreatedAt, List<string> ContentTypes);

/// <summary>POST /api/api-keys — create a key; returns the full secret ONCE.</summary>
internal class CreateApiKeyEndpoint(
    IDocumentSession session,
    ApiKeyService keys) : Endpoint<CreateApiKeyRequest, CreateApiKeyResponse>
{
    public override void Configure()
    {
        Post("/api/api-keys");
        Definition.RequireCapability(SystemCapabilities.ManageApiKeys, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CreateApiKeyRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            AddError(r => r.Name, "A name is required.");

        var scopes = (req.Scopes ?? new())
            .Select(s => s.Trim().ToLowerInvariant())
            .Where(s => s.Length > 0)
            .Distinct()
            .ToList();

        if (scopes.Count == 0)
            AddError(r => r.Scopes, "At least one scope is required.");
        foreach (var s in scopes.Where(s => !ApiKeyScopes.IsKnown(s)))
            AddError(r => r.Scopes, $"Unknown scope '{s}'.");
        if (req.ExpiresAt is { } exp && exp <= DateTime.UtcNow)
            AddError(r => r.ExpiresAt, "Expiry must be in the future.");

        var contentTypes = (req.ContentTypes ?? new())
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var type in contentTypes)
        {
            if (!await session.Query<ContentTypeDefinition>().AnyAsync(d => d.Name == type, ct))
                AddError(r => r.ContentTypes, $"There is no content type called '{type}'.");
        }
        ThrowIfAnyErrors();

        var creatorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var id) ? id : Guid.Empty;
        var tenant = (User.FindFirst("tenant")?.Value ?? Tenant.DefaultSlug).Trim().ToLowerInvariant();

        var generated = keys.Generate();
        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = req.Name.Trim(),
            KeyHash = generated.Hash,
            Prefix = generated.DisplayPrefix,
            UserId = creatorId,
            TenantSlug = tenant,
            Scopes = scopes,
            ContentTypes = contentTypes,
            ExpiresAt = req.ExpiresAt,
            Revoked = false,
            CreatedAt = DateTime.UtcNow,
        };
        session.Store(apiKey);
        await session.SaveChangesAsync(ct);

        // The one and only time the plaintext secret leaves the server.
        await Send.OkAsync(new CreateApiKeyResponse(
            apiKey.Id, generated.Secret, apiKey.Prefix, apiKey.Name, apiKey.Scopes,
            apiKey.TenantSlug, apiKey.ExpiresAt, apiKey.CreatedAt, apiKey.ContentTypes), ct);
    }
}

internal sealed record ApiKeyListItem(
    Guid Id, string Name, string Prefix, List<string> Scopes, string TenantSlug,
    DateTime? ExpiresAt, DateTime? LastUsedAt, bool Revoked, DateTime CreatedAt, List<string> ContentTypes);

/// <summary>GET /api/api-keys — list the current tenant's keys (never the secret or hash).</summary>
internal class ListApiKeysEndpoint(IQuerySession session) : Endpoint<ListRequest, PaginatedResponse<ApiKeyListItem>>
{
    public override void Configure()
    {
        Get("/api/api-keys");
        Definition.RequireCapability(SystemCapabilities.ManageApiKeys, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var tenant = (User.FindFirst("tenant")?.Value ?? Tenant.DefaultSlug).Trim().ToLowerInvariant();
        var page = await session.Query<ApiKey>()
            .Where(k => k.TenantSlug == tenant)
            .OrderByDescending(k => k.CreatedAt)
            .ToPagedResponseAsync(req, ct);

        await Send.OkAsync(new PaginatedResponse<ApiKeyListItem>
        {
            Items = page.Items
                .Select(k => new ApiKeyListItem(
                    k.Id, k.Name, k.Prefix, k.Scopes, k.TenantSlug,
                    k.ExpiresAt, k.LastUsedAt, k.Revoked, k.CreatedAt, k.ContentTypes ?? new()))
                .ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, ct);
    }
}

/// <summary>DELETE /api/api-keys/{id} — revoke a key (soft; the record is kept). Effective immediately.</summary>
internal class RevokeApiKeyEndpoint(IDocumentSession session) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/api/api-keys/{id}");
        Definition.RequireCapability(SystemCapabilities.ManageApiKeys, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var id = Route<Guid>("id");
        var tenant = (User.FindFirst("tenant")?.Value ?? Tenant.DefaultSlug).Trim().ToLowerInvariant();

        // Scoped to the caller's tenant, so an admin can't revoke another tenant's key by guessing an id.
        var key = await session.Query<ApiKey>().FirstOrDefaultAsync(k => k.Id == id && k.TenantSlug == tenant, ct);
        if (key is null) { await Send.NotFoundAsync(ct); return; }

        key.Revoked = true;
        session.Store(key);
        await session.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}

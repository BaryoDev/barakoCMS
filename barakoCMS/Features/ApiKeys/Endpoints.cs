using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.ApiKeys;

// API keys are managed by a human admin (JWT) and scoped to the admin's current tenant. They act as
// the creating user, so a key can never do more than its creator, and its scopes narrow it further to
// the content surface. The secret is shown once, on create, and only its hash is stored.

internal sealed class CreateApiKeyRequest
{
    public string Name { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = new();
    public DateTime? ExpiresAt { get; set; }
}

internal sealed record CreateApiKeyResponse(
    Guid Id, string Key, string Prefix, string Name, List<string> Scopes,
    string TenantSlug, DateTime? ExpiresAt, DateTime CreatedAt);

/// <summary>POST /api/api-keys — create a key; returns the full secret ONCE.</summary>
internal class CreateApiKeyEndpoint : Endpoint<CreateApiKeyRequest, CreateApiKeyResponse>
{
    private readonly IDocumentSession _session;
    private readonly ApiKeyService _keys;

    public CreateApiKeyEndpoint(IDocumentSession session, ApiKeyService keys)
    {
        _session = session;
        _keys = keys;
    }

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
        ThrowIfAnyErrors();

        var creatorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var id) ? id : Guid.Empty;
        var tenant = (User.FindFirst("tenant")?.Value ?? Tenant.DefaultSlug).Trim().ToLowerInvariant();

        var generated = _keys.Generate();
        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = req.Name.Trim(),
            KeyHash = generated.Hash,
            Prefix = generated.DisplayPrefix,
            UserId = creatorId,
            TenantSlug = tenant,
            Scopes = scopes,
            ExpiresAt = req.ExpiresAt,
            Revoked = false,
            CreatedAt = DateTime.UtcNow,
        };
        _session.Store(apiKey);
        await _session.SaveChangesAsync(ct);

        // The one and only time the plaintext secret leaves the server.
        await Send.OkAsync(new CreateApiKeyResponse(
            apiKey.Id, generated.Secret, apiKey.Prefix, apiKey.Name, apiKey.Scopes,
            apiKey.TenantSlug, apiKey.ExpiresAt, apiKey.CreatedAt), ct);
    }
}

internal sealed record ApiKeyListItem(
    Guid Id, string Name, string Prefix, List<string> Scopes, string TenantSlug,
    DateTime? ExpiresAt, DateTime? LastUsedAt, bool Revoked, DateTime CreatedAt);

/// <summary>GET /api/api-keys — list the current tenant's keys (never the secret or hash).</summary>
internal class ListApiKeysEndpoint : Endpoint<ListRequest, PaginatedResponse<ApiKeyListItem>>
{
    private readonly IQuerySession _session;
    public ListApiKeysEndpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/api-keys");
        Definition.RequireCapability(SystemCapabilities.ManageApiKeys, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var tenant = (User.FindFirst("tenant")?.Value ?? Tenant.DefaultSlug).Trim().ToLowerInvariant();
        var page = await _session.Query<ApiKey>()
            .Where(k => k.TenantSlug == tenant)
            .OrderByDescending(k => k.CreatedAt)
            .ToPagedResponseAsync(req, ct);

        await Send.OkAsync(new PaginatedResponse<ApiKeyListItem>
        {
            Items = page.Items
                .Select(k => new ApiKeyListItem(
                    k.Id, k.Name, k.Prefix, k.Scopes, k.TenantSlug,
                    k.ExpiresAt, k.LastUsedAt, k.Revoked, k.CreatedAt))
                .ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, ct);
    }
}

/// <summary>DELETE /api/api-keys/{id} — revoke a key (soft; the record is kept). Effective immediately.</summary>
internal class RevokeApiKeyEndpoint : EndpointWithoutRequest
{
    private readonly IDocumentSession _session;
    public RevokeApiKeyEndpoint(IDocumentSession session) => _session = session;

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
        var key = await _session.Query<ApiKey>().FirstOrDefaultAsync(k => k.Id == id && k.TenantSlug == tenant, ct);
        if (key is null) { await Send.NotFoundAsync(ct); return; }

        key.Revoked = true;
        _session.Store(key);
        await _session.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}

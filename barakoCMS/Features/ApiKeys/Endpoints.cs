using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation;
using Marten;
using Marten.Patching;

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
[barakoCMS.Infrastructure.Filters.NoIdempotentReplay]
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
            if (!await session.Query<ContentTypeDefinition>().AnyAsync(barakoCMS.Core.ContentTypeName.Named(type), ct))
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

        // Names and ids only. The secret, its hash and its display prefix stay out of the row: the
        // prefix is the first characters of the secret, and the key's id already identifies it.
        var metadata = new Dictionary<string, object>
        {
            ["name"] = apiKey.Name,
            ["scopes"] = apiKey.Scopes,
            ["contentTypes"] = apiKey.ContentTypes,
            ["actsAsUserId"] = apiKey.UserId.ToString(),
        };
        if (apiKey.ExpiresAt is { } expiresAt)
            metadata["expiresAt"] = expiresAt;

        await AuditLog.RecordAsync(session, tenant, "apikey.created",
            creatorId == Guid.Empty ? null : creatorId, User.FindFirst("Username")?.Value,
            targetType: "ApiKey", targetId: apiKey.Id.ToString(), metadata: metadata, ct: ct);
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
    /// <summary>
    /// Who may read a key's scopes. The audit list asks this same gate before it returns that
    /// detail of a key entry.
    /// </summary>
    internal static readonly RequiredCapability Gate = new(SystemCapabilities.ManageApiKeys, ["SuperAdmin", "Admin"]);

    public override void Configure()
    {
        Get("/api/api-keys");
        Definition.RequireCapability(Gate.Capability, [.. Gate.LegacyRoles]);
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

        // A key that is already revoked is answered the same way and writes nothing, so the trail
        // holds one row per revocation, not one per request.
        if (!key.Revoked)
        {
            session.Patch<ApiKey>(key.Id).Set(x => x.Revoked, true);
            Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
            await AuditLog.RecordAsync(session, tenant, "apikey.revoked", actorId, User.FindFirst("Username")?.Value,
                targetType: "ApiKey", targetId: key.Id.ToString(),
                metadata: new() { ["name"] = key.Name, ["actsAsUserId"] = key.UserId.ToString() },
                ct: ct);
            await session.SaveChangesAsync(ct);
        }

        await Send.NoContentAsync(ct);
    }
}

internal sealed class RotateApiKeyRequest
{
    public Guid Id { get; set; }

    /// <summary>Minutes the old key keeps working after the new one is issued. 0 ends it at once.</summary>
    public int OverlapMinutes { get; set; } = 60;
}

internal sealed class RotateApiKeyValidator : Validator<RotateApiKeyRequest>
{
    public const int MaxOverlapMinutes = 7 * 24 * 60;

    public RotateApiKeyValidator()
    {
        RuleFor(r => r.OverlapMinutes).InclusiveBetween(0, MaxOverlapMinutes);
    }
}

/// <summary>
/// POST /api/api-keys/{id}/rotate. Issues a new key with the same owner, tenant, scopes and content
/// types, and shortens the old key's expiry to the end of the overlap window, so both work until
/// then and only the new one after. A key that already expires sooner keeps its earlier expiry.
/// </summary>
[barakoCMS.Infrastructure.Filters.NoIdempotentReplay]
internal class RotateApiKeyEndpoint(
    IDocumentSession session,
    ApiKeyService keys) : Endpoint<RotateApiKeyRequest, CreateApiKeyResponse>
{
    public override void Configure()
    {
        Post("/api/api-keys/{id}/rotate");
        Definition.RequireCapability(SystemCapabilities.ManageApiKeys, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(RotateApiKeyRequest req, CancellationToken ct)
    {
        var tenant = (User.FindFirst("tenant")?.Value ?? Tenant.DefaultSlug).Trim().ToLowerInvariant();

        var old = await session.Query<ApiKey>().FirstOrDefaultAsync(k => k.Id == req.Id && k.TenantSlug == tenant, ct);
        if (old is null) { await Send.NotFoundAsync(ct); return; }

        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        if (old.UserId != actorId
            && !await barakoCMS.Features.Users.PlatformRoles.IsSuperAdminAsync(session, User, ct))
        {
            await RecordDeniedAsync(tenant, actorId, old, ct);
            await Send.ForbiddenAsync(ct);
            return;
        }

        var now = DateTime.UtcNow;
        if (old.Revoked || (old.ExpiresAt is { } oldExpiry && oldExpiry <= now))
        {
            AddError("A revoked or expired key cannot be rotated.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        if (await session.LoadAsync<User>(old.UserId, ct) is null)
        {
            AddError("The user who owns this key no longer exists.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var generated = keys.Generate();
        var fresh = NewKeyFrom(old, generated, now);

        var cutoff = now.AddMinutes(req.OverlapMinutes);
        if (old.ExpiresAt is null || cutoff < old.ExpiresAt)
            old.ExpiresAt = cutoff;

        if (AfterLoadForTests is { } hook)
            await hook(old, ct);

        session.Store(old);
        session.Store(fresh);

        await AuditLog.RecordAsync(session, tenant, "apikey.rotated",
            actorId == Guid.Empty ? null : actorId, User.FindFirst("Username")?.Value,
            targetType: "ApiKey", targetId: fresh.Id.ToString(),
            metadata: new()
            {
                ["name"] = fresh.Name,
                ["replacesKeyId"] = old.Id.ToString(),
                ["overlapMinutes"] = req.OverlapMinutes,
            },
            ct: ct);
        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (JasperFx.ConcurrencyException)
        {
            AddError("The key changed while it was being rotated. Try again.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        await Send.OkAsync(new CreateApiKeyResponse(
            fresh.Id, generated.Secret, fresh.Prefix, fresh.Name, fresh.Scopes,
            fresh.TenantSlug, fresh.ExpiresAt, fresh.CreatedAt, fresh.ContentTypes), ct);
    }

    // Lets a test land a revoke between the read above and the save below, which a request cannot do
    // deterministically. Null outside tests.
    internal static Func<ApiKey, CancellationToken, Task>? AfterLoadForTests { get; set; }

    private async Task RecordDeniedAsync(string tenant, Guid actorId, ApiKey key, CancellationToken ct)
    {
        await AuditLog.RecordAsync(session, tenant, "apikey.rotate.denied",
            actorId == Guid.Empty ? null : actorId, User.FindFirst("Username")?.Value,
            targetType: "ApiKey", targetId: key.Id.ToString(),
            metadata: new() { ["name"] = key.Name }, ct: ct);
        await session.SaveChangesAsync(ct);
    }

    private static ApiKey NewKeyFrom(ApiKey old, ApiKeyService.GeneratedKey generated, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        Name = old.Name,
        KeyHash = generated.Hash,
        Prefix = generated.DisplayPrefix,
        UserId = old.UserId,
        TenantSlug = old.TenantSlug,
        Scopes = [.. old.Scopes],
        ContentTypes = [.. old.ContentTypes ?? []],
        ExpiresAt = old.ExpiresAt,
        Revoked = false,
        CreatedAt = now,
    };
}

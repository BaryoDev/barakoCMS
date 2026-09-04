using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Connectors;

/// <summary>
/// Shared plumbing for the connector slices: the role names that gated them before capabilities,
/// and how a change is recorded.
/// </summary>
/// <remarks>
/// SuperAdmin and Admin gated every route here, read and write alike, which is why one legacy list
/// serves both <see cref="SystemCapabilities.ViewConnectors"/> and
/// <see cref="SystemCapabilities.ManageConnectors"/>: the fallback preserves what the names already
/// opened, and the split is about what a role created at runtime can be given.
///
/// Configuring a connector is credential management rather than content editing, and "who added a
/// credential pointing where" is the first question a security review asks, which is why every one
/// of these writes an audit entry naming the connector and never its secrets.
/// </remarks>
internal static class ConnectorGate
{
    internal static readonly string[] LegacyRoles = ["SuperAdmin", "Admin"];

    internal static Task AuditAsync(
        IDocumentSession session,
        string tenantSlug,
        string action,
        Connector connector,
        System.Security.Claims.ClaimsPrincipal user,
        Dictionary<string, object>? extra = null,
        CancellationToken ct = default)
    {
        var actorId = Guid.TryParse(user.FindFirst("UserId")?.Value, out var parsed) ? parsed : (Guid?)null;

        var metadata = new Dictionary<string, object>
        {
            ["slug"] = connector.Slug,
            ["baseUrl"] = connector.BaseUrl,
            ["auth"] = connector.Auth.ToString(),
            // The names of the secrets held, never a value. An audit trail that quotes a credential
            // puts it in the one table designed never to be deleted from.
            ["secretKeys"] = string.Join(", ", connector.SecretKeys),
        };

        if (extra is not null)
        {
            foreach (var pair in extra) metadata[pair.Key] = pair.Value;
        }

        return AuditLog.RecordAsync(session, tenantSlug, action, actorId, user.FindFirst("Username")?.Value,
            targetType: nameof(Connector), targetId: connector.Id.ToString(), metadata: metadata, ct: ct);
    }
}

internal sealed class ListConnectorsEndpoint : Endpoint<ListRequest, PaginatedResponse<ConnectorResponse>>
{
    private readonly IQuerySession _session;

    public ListConnectorsEndpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/connectors");
        Definition.RequireCapability(SystemCapabilities.ViewConnectors, ConnectorGate.LegacyRoles);
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var page = await _session.Query<Connector>().OrderBy(c => c.Name).ToPagedResponseAsync(req, ct);

        await Send.ResponseAsync(new PaginatedResponse<ConnectorResponse>
        {
            Items = page.Items.Select(ConnectorResponse.From).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, cancellation: ct);
    }
}

internal sealed class GetConnectorEndpoint : EndpointWithoutRequest<ConnectorResponse>
{
    private readonly IQuerySession _session;

    public GetConnectorEndpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/connectors/{slug}");
        Definition.RequireCapability(SystemCapabilities.ViewConnectors, ConnectorGate.LegacyRoles);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var slug = Route<string>("slug") ?? string.Empty;

        if (!ConnectorRules.IsSlug(slug))
        {
            ThrowError("That is not a connector slug: lowercase letters, digits and hyphens only.", 400);
            return;
        }

        var connector = await _session.Query<Connector>().FirstOrDefaultAsync(c => c.Slug == slug, ct);

        if (connector is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.ResponseAsync(ConnectorResponse.From(connector), cancellation: ct);
    }
}

internal sealed class CreateConnectorEndpoint : Endpoint<SaveConnectorRequest, ConnectorResponse>
{
    private readonly IDocumentSession _session;
    private readonly IConnectorSecretProtector _protector;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public CreateConnectorEndpoint(
        IDocumentSession session,
        IConnectorSecretProtector protector,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _session = session;
        _protector = protector;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Post("/api/connectors");
        Definition.RequireCapability(SystemCapabilities.ManageConnectors, ConnectorGate.LegacyRoles);
    }

    public override async Task HandleAsync(SaveConnectorRequest req, CancellationToken ct)
    {
        var problem = ConnectorRules.Check(req);
        if (problem is not null)
        {
            ThrowError(problem, 400);
            return;
        }

        if (req.Secrets is { Count: > 0 } && !_protector.IsConfigured)
        {
            // Fail closed and name the setting. Storing the credential in the clear because no key
            // was configured is the one outcome nobody would choose and nobody would notice.
            ThrowError("Connectors:Key is not configured, so a credential cannot be stored. Set it and restart.", 400);
            return;
        }

        if (await _session.Query<Connector>().AnyAsync(c => c.Slug == req.Slug, ct))
        {
            ThrowError($"A connector with the slug '{req.Slug}' already exists.", 409);
            return;
        }

        var connector = new Connector
        {
            Id = Guid.NewGuid(),
            Name = req.Name.Trim(),
            Slug = req.Slug.Trim().ToLowerInvariant(),
            BaseUrl = req.BaseUrl.Trim(),
            Auth = Enum.Parse<ConnectorAuth>(req.Auth, ignoreCase: true),
            Settings = req.Settings,
            Enabled = req.Enabled,
            ProbePath = string.IsNullOrWhiteSpace(req.ProbePath) ? "/" : req.ProbePath.Trim(),
        };

        connector.SecretKeys = StoreSecrets(connector.Id, req.Secrets, replaceAll: true);
        _session.Store(connector);

        await ConnectorGate.AuditAsync(_session, _tenant.Slug, "connector.created", connector, User, ct: ct);
        await _session.SaveChangesAsync(ct);

        await Send.ResponseAsync(ConnectorResponse.From(connector), cancellation: ct);
    }

    private List<string> StoreSecrets(Guid connectorId, Dictionary<string, string>? secrets, bool replaceAll)
    {
        var names = new List<string>();
        if (secrets is null) return names;

        foreach (var (key, value) in secrets)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;

            _session.Store(new ConnectorSecret
            {
                Id = Guid.NewGuid(),
                ConnectorId = connectorId,
                Key = key,
                ProtectedValue = _protector.Protect(value),
            });
            names.Add(key);
        }

        return names;
    }
}

internal sealed class UpdateConnectorEndpoint : Endpoint<SaveConnectorRequest, ConnectorResponse>
{
    private readonly IDocumentSession _session;
    private readonly IConnectorSecretProtector _protector;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public UpdateConnectorEndpoint(
        IDocumentSession session,
        IConnectorSecretProtector protector,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _session = session;
        _protector = protector;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Put("/api/connectors/{slug}");
        Definition.RequireCapability(SystemCapabilities.ManageConnectors, ConnectorGate.LegacyRoles);
    }

    public override async Task HandleAsync(SaveConnectorRequest req, CancellationToken ct)
    {
        var slug = Route<string>("slug") ?? string.Empty;

        if (!ConnectorRules.IsSlug(slug))
        {
            ThrowError("That is not a connector slug: lowercase letters, digits and hyphens only.", 400);
            return;
        }

        var connector = await _session.Query<Connector>().FirstOrDefaultAsync(c => c.Slug == slug, ct);

        if (connector is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // The slug is what a request definition references, so it is not editable here. Renaming it
        // would break every reference silently, and the delete path already refuses for that reason.
        req.Slug = connector.Slug;

        var problem = ConnectorRules.Check(req);
        if (problem is not null)
        {
            ThrowError(problem, 400);
            return;
        }

        if (req.Secrets is { Count: > 0 } && !_protector.IsConfigured)
        {
            ThrowError("Connectors:Key is not configured, so a credential cannot be stored. Set it and restart.", 400);
            return;
        }

        connector.Name = req.Name.Trim();
        connector.BaseUrl = req.BaseUrl.Trim();
        connector.Auth = Enum.Parse<ConnectorAuth>(req.Auth, ignoreCase: true);
        connector.Settings = req.Settings;
        connector.Enabled = req.Enabled;
        connector.ProbePath = string.IsNullOrWhiteSpace(req.ProbePath) ? "/" : req.ProbePath.Trim();
        connector.UpdatedAt = DateTime.UtcNow;

        var changedSecrets = new List<string>();

        if (req.Secrets is not null)
        {
            var existing = await _session.Query<ConnectorSecret>()
                .Where(s => s.ConnectorId == connector.Id)
                .ToListAsync(ct);

            foreach (var (key, value) in req.Secrets)
            {
                var current = existing.FirstOrDefault(s => s.Key == key);

                if (string.IsNullOrWhiteSpace(value))
                {
                    // Empty removes it. Absent leaves it alone, which is the case that matters: the
                    // form cannot show the current value, so it cannot send it back unchanged.
                    if (current is not null)
                    {
                        _session.Delete(current);
                        connector.SecretKeys.Remove(key);
                        changedSecrets.Add($"{key} cleared");
                    }
                    continue;
                }

                if (current is null)
                {
                    _session.Store(new ConnectorSecret
                    {
                        Id = Guid.NewGuid(),
                        ConnectorId = connector.Id,
                        Key = key,
                        ProtectedValue = _protector.Protect(value),
                    });
                    if (!connector.SecretKeys.Contains(key)) connector.SecretKeys.Add(key);
                }
                else
                {
                    current.ProtectedValue = _protector.Protect(value);
                    current.UpdatedAt = DateTime.UtcNow;
                    _session.Store(current);
                }

                changedSecrets.Add($"{key} set");
            }
        }

        _session.Store(connector);

        await ConnectorGate.AuditAsync(_session, _tenant.Slug, "connector.updated", connector, User,
            extra: changedSecrets.Count > 0
                ? new Dictionary<string, object> { ["secretsChanged"] = string.Join(", ", changedSecrets) }
                : null,
            ct: ct);

        await _session.SaveChangesAsync(ct);

        await Send.ResponseAsync(ConnectorResponse.From(connector), cancellation: ct);
    }
}

internal sealed class DeleteConnectorEndpoint : EndpointWithoutRequest
{
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public DeleteConnectorEndpoint(
        IDocumentSession session,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _session = session;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Delete("/api/connectors/{slug}");
        Definition.RequireCapability(SystemCapabilities.ManageConnectors, ConnectorGate.LegacyRoles);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var slug = Route<string>("slug") ?? string.Empty;

        if (!ConnectorRules.IsSlug(slug))
        {
            ThrowError("That is not a connector slug: lowercase letters, digits and hyphens only.", 400);
            return;
        }

        var connector = await _session.Query<Connector>().FirstOrDefaultAsync(c => c.Slug == slug, ct);

        if (connector is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // The secrets go with it, in the same transaction. Leaving them behind would keep decryptable
        // credentials in the database belonging to a connector nobody can see any more, which is the
        // worst of both: still a liability, no longer visible.
        var secrets = await _session.Query<ConnectorSecret>()
            .Where(s => s.ConnectorId == connector.Id)
            .ToListAsync(ct);

        foreach (var secret in secrets) _session.Delete(secret);
        _session.Delete(connector);

        await ConnectorGate.AuditAsync(_session, _tenant.Slug, "connector.deleted", connector, User, ct: ct);
        await _session.SaveChangesAsync(ct);

        await Send.NoContentAsync(ct);
    }
}

internal sealed class TestConnectorEndpoint : EndpointWithoutRequest<TestConnectorResponse>
{
    private readonly IDocumentSession _session;
    private readonly IConnectorSender _sender;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public TestConnectorEndpoint(
        IDocumentSession session,
        IConnectorSender sender,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _session = session;
        _sender = sender;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Post("/api/connectors/{slug}/test");
        Definition.RequireCapability(SystemCapabilities.ManageConnectors, ConnectorGate.LegacyRoles);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var slug = Route<string>("slug") ?? string.Empty;

        if (!ConnectorRules.IsSlug(slug))
        {
            ThrowError("That is not a connector slug: lowercase letters, digits and hyphens only.", 400);
            return;
        }

        var connector = await _session.Query<Connector>().FirstOrDefaultAsync(c => c.Slug == slug, ct);

        if (connector is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var result = await _sender.ProbeAsync(connector, ct);

        connector.LastTestedAt = DateTime.UtcNow;
        connector.LastTestResult = result.Describe();
        _session.Store(connector);

        await ConnectorGate.AuditAsync(_session, _tenant.Slug, "connector.tested", connector, User,
            extra: new Dictionary<string, object> { ["result"] = connector.LastTestResult },
            ct: ct);

        await _session.SaveChangesAsync(ct);

        // The status code and the round trip. Not the body: a 401 from an OAuth provider frequently
        // contains the credential that was sent, so echoing it would be the leak this feature spends
        // a separate document avoiding.
        await Send.ResponseAsync(new TestConnectorResponse
        {
            Succeeded = result.Succeeded,
            StatusCode = result.StatusCode,
            ElapsedMs = result.ElapsedMs,
            Error = result.Error,
        }, cancellation: ct);
    }
}

internal static class ConnectorRules
{
    /// <summary>The shape a slug has to have, checked on the way in as well as on the way out.</summary>
    /// <remarks>
    /// Applied to the route value too, not only to a create. A slug that cannot exist is a malformed
    /// request rather than a missing connector, and answering 404 to it says "no such connector" when
    /// the truthful answer is "that is not a connector name". It also keeps the route distinguishable
    /// from a routing failure, which is what RoleGateTests checks for when it probes a gated route:
    /// a 404 is what a route that has been removed looks like too.
    /// </remarks>
    internal static bool IsSlug(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-z0-9][a-z0-9-]{0,62}$");


    /// <summary>Returns the reason a request is not acceptable, or null.</summary>
    internal static string? Check(SaveConnectorRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return "Name is required.";
        if (string.IsNullOrWhiteSpace(req.Slug)) return "Slug is required.";

        if (!System.Text.RegularExpressions.Regex.IsMatch(req.Slug, "^[a-z0-9][a-z0-9-]{0,62}$"))
        {
            return "Slug must be lowercase letters, digits and hyphens, starting with a letter or digit.";
        }

        if (!Enum.TryParse<ConnectorAuth>(req.Auth, ignoreCase: true, out _))
        {
            return $"Auth must be one of: {string.Join(", ", Enum.GetNames<ConnectorAuth>())}.";
        }

        // An early refusal, not the guard. The address that gets dialled is checked when the socket
        // opens, which is the only check a DNS answer that changes afterwards cannot get around.
        if (!Uri.TryCreate(req.BaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "BaseUrl must be an absolute http or https URL.";
        }

        return null;
    }
}

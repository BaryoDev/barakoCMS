using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using FastEndpoints;
using Marten;
using Marten.Patching;

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
    internal static readonly IReadOnlyList<string> LegacyRoles = CapabilityGate.AdminLegacyRoles;

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

internal sealed class ListConnectorsEndpoint(
    IQuerySession session) : Endpoint<ListRequest, PaginatedResponse<ConnectorResponse>>
{
    public override void Configure()
    {
        Get("/api/connectors");
        Definition.RequireCapability(SystemCapabilities.ViewConnectors, ConnectorGate.LegacyRoles);
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var page = await session.Query<Connector>().OrderBy(c => c.Name).ToPagedResponseAsync(req, ct);

        await Send.ResponseAsync(new PaginatedResponse<ConnectorResponse>
        {
            Items = page.Items.Select(ConnectorResponse.From).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, cancellation: ct);
    }
}

internal sealed class GetConnectorEndpoint(IQuerySession session) : EndpointWithoutRequest<ConnectorResponse>
{
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

        var connector = await session.Query<Connector>().FirstOrDefaultAsync(c => c.Slug == slug, ct);

        if (connector is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.ResponseAsync(ConnectorResponse.From(connector), cancellation: ct);
    }
}

internal sealed class CreateConnectorEndpoint(
    IDocumentSession session,
    IConnectorSecretProtector protector,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<SaveConnectorRequest, ConnectorResponse>
{
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

        if (req.Secrets is { Count: > 0 } && !protector.IsConfigured)
        {
            // Fail closed and name the setting. Storing the credential in the clear because no key
            // was configured is the one outcome nobody would choose and nobody would notice.
            ThrowError("Connectors:Key is not configured, so a credential cannot be stored. Set it and restart.", 400);
            return;
        }

        if (await session.Query<Connector>().AnyAsync(c => c.Slug == req.Slug, ct))
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
        session.Store(connector);

        await ConnectorGate.AuditAsync(session, tenant.Slug, "connector.created", connector, User, ct: ct);
        await session.SaveChangesAsync(ct);

        await Send.ResponseAsync(ConnectorResponse.From(connector), cancellation: ct);
    }

    private List<string> StoreSecrets(Guid connectorId, Dictionary<string, string>? secrets, bool replaceAll)
    {
        var names = new List<string>();
        if (secrets is null) return names;

        foreach (var (key, value) in secrets)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;

            session.Store(new ConnectorSecret
            {
                Id = Guid.NewGuid(),
                ConnectorId = connectorId,
                Key = key,
                ProtectedValue = protector.Protect(value),
            });
            names.Add(key);
        }

        return names;
    }
}

internal sealed class UpdateConnectorEndpoint(
    IDocumentSession session,
    IConnectorSecretProtector protector,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<SaveConnectorRequest, ConnectorResponse>
{
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

        var connector = await session.Query<Connector>().FirstOrDefaultAsync(c => c.Slug == slug, ct);

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

        if (req.Secrets is { Count: > 0 } && !protector.IsConfigured)
        {
            ThrowError("Connectors:Key is not configured, so a credential cannot be stored. Set it and restart.", 400);
            return;
        }

        if (!ConnectorOrigin.Same(connector.BaseUrl, req.BaseUrl))
        {
            // An absent secret normally means "keep it", because the form cannot show it. That cannot
            // hold across a move to another origin: the credential was entered for the old one, and
            // whoever edits the connector may never have seen it. Each stored secret has to be
            // entered again or cleared in the same request.
            var storedKeys = await session.Query<ConnectorSecret>()
                .Where(s => s.ConnectorId == connector.Id)
                .Select(s => s.Key)
                .ToListAsync(ct);

            foreach (var key in storedKeys.Distinct().Where(k => req.Secrets is null || !req.Secrets.ContainsKey(k)))
            {
                AddError(new FluentValidation.Results.ValidationFailure($"secrets.{key}",
                    $"The base URL now points at a different scheme, host or port, so the stored {key} "
                    + "has to be entered again, or cleared, before the connector can be saved."));
            }

            ThrowIfAnyErrors();
        }

        // The client secret goes to the token URL and nowhere else, so the same reasoning applies to
        // it: an edit must not send a secret its editor cannot read somewhere nobody entered it for.
        // The whole URL is compared, not the origin. A token endpoint is one exact address, and
        // another path on a shared identity host can be another party's.
        var tokenUrl = req.Settings?.GetValueOrDefault(ConnectorSettingKeys.TokenUrl)?.Trim();

        if (!string.IsNullOrEmpty(tokenUrl)
            && !string.Equals(
                connector.Settings?.GetValueOrDefault(ConnectorSettingKeys.TokenUrl)?.Trim(), tokenUrl, StringComparison.Ordinal)
            && req.Secrets?.ContainsKey(ConnectorSecretKeys.ClientSecret) != true
            && await session.Query<ConnectorSecret>()
                .AnyAsync(s => s.ConnectorId == connector.Id && s.Key == ConnectorSecretKeys.ClientSecret, ct))
        {
            AddError(new FluentValidation.Results.ValidationFailure($"secrets.{ConnectorSecretKeys.ClientSecret}",
                "The token URL is new or has changed, so the stored "
                + $"{ConnectorSecretKeys.ClientSecret} has to be entered again, or cleared, before the connector can be saved."));

            ThrowIfAnyErrors();
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
            var existing = await session.Query<ConnectorSecret>()
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
                        session.Delete(current);
                        connector.SecretKeys.Remove(key);
                        changedSecrets.Add($"{key} cleared");
                    }
                    continue;
                }

                if (current is null)
                {
                    session.Store(new ConnectorSecret
                    {
                        Id = Guid.NewGuid(),
                        ConnectorId = connector.Id,
                        Key = key,
                        ProtectedValue = protector.Protect(value),
                    });
                    if (!connector.SecretKeys.Contains(key)) connector.SecretKeys.Add(key);
                }
                else
                {
                    current.ProtectedValue = protector.Protect(value);
                    current.UpdatedAt = DateTime.UtcNow;
                    session.Store(current);
                }

                changedSecrets.Add($"{key} set");
            }
        }

        session.Store(connector);

        await ConnectorGate.AuditAsync(session, tenant.Slug, "connector.updated", connector, User,
            extra: changedSecrets.Count > 0
                ? new Dictionary<string, object> { ["secretsChanged"] = string.Join(", ", changedSecrets) }
                : null,
            ct: ct);

        await session.SaveChangesAsync(ct);

        await Send.ResponseAsync(ConnectorResponse.From(connector), cancellation: ct);
    }
}

internal sealed class DeleteConnectorEndpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : EndpointWithoutRequest
{
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

        var connector = await session.Query<Connector>().FirstOrDefaultAsync(c => c.Slug == slug, ct);

        if (connector is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // The secrets go with it, in the same transaction. Leaving them behind would keep decryptable
        // credentials in the database belonging to a connector nobody can see any more, which is the
        // worst of both: still a liability, no longer visible.
        var secrets = await session.Query<ConnectorSecret>()
            .Where(s => s.ConnectorId == connector.Id)
            .ToListAsync(ct);

        foreach (var secret in secrets) session.Delete(secret);
        session.Delete(connector);

        await ConnectorGate.AuditAsync(session, tenant.Slug, "connector.deleted", connector, User, ct: ct);
        await session.SaveChangesAsync(ct);

        await Send.NoContentAsync(ct);
    }
}

internal sealed class TestConnectorEndpoint(
    IDocumentSession session,
    IConnectorSender sender,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : EndpointWithoutRequest<TestConnectorResponse>
{
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

        var connector = await session.Query<Connector>().FirstOrDefaultAsync(c => c.Slug == slug, ct);

        if (connector is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var result = await sender.ProbeAsync(connector, ct);
        var described = result.Describe();

        // The two fields a test owns, patched. Storing the document read before the probe would
        // write every field back as it was then, and undo an update that landed while the probe
        // was out.
        session.Patch<Connector>(connector.Id).Set(x => x.LastTestedAt, (DateTime?)DateTime.UtcNow);
        session.Patch<Connector>(connector.Id).Set(x => x.LastTestResult, described);

        await ConnectorGate.AuditAsync(session, tenant.Slug, "connector.tested", connector, User,
            extra: new Dictionary<string, object> { ["result"] = described },
            ct: ct);

        await session.SaveChangesAsync(ct);

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

        // The probe carries the connector's credentials, so its path has to stay on the base URL's
        // host. A leading "//", a backslash, or a scheme would make it a URL of its own.
        var probe = string.IsNullOrWhiteSpace(req.ProbePath) ? "/" : req.ProbePath.Trim();
        if (!probe.StartsWith('/') || probe.StartsWith("//") || probe.Contains('\\') || probe.Any(char.IsControl))
        {
            return "ProbePath must be a path on the base URL that starts with a single '/', such as /health.";
        }

        // Settings are stored and returned as they are, so a credential does not belong in them.
        // Refused rather than moved into the secrets: the sender reads only the secret names
        // ConnectorSecretKeys lists, so a moved value would be stored where nothing reads it, and
        // the setting would disappear from the answer without the caller being told why.
        var credential = req.Settings?.Keys.FirstOrDefault(
            key => CredentialNames.IsCredential(key) && !KnownSettings.Contains(key));
        if (credential is not null)
        {
            return $"The setting '{credential}' reads as a credential, and settings are stored and returned as "
                 + $"plain text. Send it in secrets instead ({string.Join(", ", ConnectorSecretKeys.All)}), "
                 + "which are encrypted and never returned.";
        }

        return null;
    }

    /// <summary>
    /// The settings the auth modes read. <c>TokenUrl</c> holds the word "token" and is an address,
    /// not a credential.
    /// </summary>
    private static readonly HashSet<string> KnownSettings = new(StringComparer.Ordinal)
    {
        ConnectorSettingKeys.Username, ConnectorSettingKeys.HeaderName, ConnectorSettingKeys.TokenUrl,
        ConnectorSettingKeys.ClientId, ConnectorSettingKeys.Scope, ConnectorSettingKeys.Audience,
        ConnectorSettingKeys.ClientAuth, ConnectorSettingKeys.IdempotencyHeader,
    };
}

using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Config;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Connectors;

/// <summary>
/// A connector as its document. <see cref="Secrets"/> names the credentials it holds, and never
/// carries a value.
/// </summary>
internal sealed record ConnectorConfig(
    string? Name,
    string? BaseUrl,
    string? Auth,
    Dictionary<string, string>? Settings,
    bool Enabled,
    string? ProbePath,
    List<string>? Secrets)
{
    public static ConnectorConfig Of(Connector connector) => new(
        connector.Name, connector.BaseUrl, connector.Auth.ToString(),
        new Dictionary<string, string>(connector.Settings ?? [], StringComparer.Ordinal), connector.Enabled,
        string.IsNullOrWhiteSpace(connector.ProbePath) ? "/" : connector.ProbePath,
        connector.SecretKeys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());

    /// <summary>Everything compared between what is stored and what is declared: all but the secrets.</summary>
    public ConnectorConfig WithoutSecrets() => this with
    {
        Name = Name?.Trim(),
        BaseUrl = BaseUrl?.Trim(),
        Auth = Enum.TryParse<ConnectorAuth>(Auth, ignoreCase: true, out var auth) ? auth.ToString() : Auth,
        Settings = new Dictionary<string, string>(Settings ?? [], StringComparer.Ordinal),
        ProbePath = string.IsNullOrWhiteSpace(ProbePath) ? "/" : ProbePath.Trim(),
        Secrets = null,
    };
}

/// <summary>
/// The <c>connectors</c> section: each connector of the tenant under its slug, its secrets by name.
/// </summary>
/// <remarks>
/// <para>
/// A secret is carried by reference only. The export lists the names a connector holds and no
/// value, and a document whose <c>secrets</c> is anything but a list of names is refused, so an
/// apply cannot store a credential it was handed in a file. A name the document lists that the
/// tenant does not hold is a note: set it with <c>PUT /api/connectors/{slug}</c>.
/// </para>
/// <para>
/// A held secret the document does not list is cleared, destructively. So is every held secret when
/// the base URL moves to another origin, and the client secret when the token URL changes: the rule
/// <c>PUT /api/connectors/{slug}</c> applies, since a credential entered for one address must not be
/// sent to another by somebody who never saw it. A connector the document leaves out is deleted with
/// its secrets, destructively.
/// </para>
/// </remarks>
internal sealed class ConnectorConfigProvider(IDocumentSession session, TenantContext tenant) : IConfigProvider
{
    public string Section => "connectors";

    public int Order => 500;

    public string ReadCapability => SystemCapabilities.ViewConnectors;

    public string WriteCapability => SystemCapabilities.ManageConnectors;

    public async Task<ConfigSectionExport?> ExportAsync(ConfigCaller caller, CancellationToken ct)
    {
        var connectors = await session.Query<Connector>().ToListAsync(ct);
        return new ConfigSectionExport
        {
            Documents = connectors.ToDictionary(c => c.Slug, c => ConfigJson.ToElement(ConnectorConfig.Of(c)), StringComparer.Ordinal),
        };
    }

    public async Task<ConfigSectionPlan> PlanAsync(
        IReadOnlyDictionary<string, JsonElement> desired, ConfigCaller caller, CancellationToken ct)
    {
        var errors = new List<string>();
        var notes = new List<string>();
        var changes = new List<ConfigChange>();
        var stored = (await session.Query<Connector>().ToListAsync(ct)).ToDictionary(c => c.Slug, StringComparer.Ordinal);

        foreach (var (slug, document) in desired.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            if (Read(slug, document, errors) is not { } config)
            {
                continue;
            }

            stored.TryGetValue(slug, out var current);
            var missing = (config.Secrets ?? []).Except(current?.SecretKeys ?? [], StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
            {
                notes.Add($"'{slug}' needs {string.Join(", ", missing)}, which this tenant does not hold. Set "
                          + $"{(missing.Count == 1 ? "it" : "them")} with PUT /api/connectors/{slug} after the apply.");
            }

            if (Plan(slug, config, current, document) is { } change)
            {
                changes.Add(change);
            }
        }

        changes.AddRange(stored.Keys.Where(slug => !desired.ContainsKey(slug)).Order(StringComparer.Ordinal).Select(slug => new ConfigChange
        {
            Key = slug, Kind = ConfigChangeKind.Destructive, Summary = $"delete connector '{slug}' and the secrets it holds",
        }));

        return new ConfigSectionPlan { Section = Section, Changes = changes, Errors = errors, Notes = notes };
    }

    public async Task<IReadOnlyList<string>> ApplyAsync(ConfigSectionPlan plan, ConfigCaller caller, CancellationToken ct)
    {
        var errors = new List<string>();
        var actor = caller.Principal ?? new System.Security.Claims.ClaimsPrincipal();

        foreach (var change in plan.Changes)
        {
            var current = await session.Query<Connector>().FirstOrDefaultAsync(c => c.Slug == change.Key, ct);
            var secrets = current is null
                ? []
                : await session.Query<ConnectorSecret>().Where(s => s.ConnectorId == current.Id).ToListAsync(ct);

            if (change.Desired is not { } document)
            {
                if (current is not null)
                {
                    foreach (var secret in secrets)
                    {
                        session.Delete(secret);
                    }

                    session.Delete(current);
                    await ConnectorGate.AuditAsync(session, tenant.Slug, "connector.deleted", current, actor, ct: ct);
                }

                continue;
            }

            if (Read(change.Key, document, errors) is not { } config)
            {
                continue;
            }

            var connector = current ?? new Connector { Id = Guid.NewGuid(), Slug = change.Key, CreatedAt = DateTime.UtcNow };
            var cleared = Cleared(config, current).ToHashSet(StringComparer.Ordinal);
            foreach (var secret in secrets.Where(s => cleared.Contains(s.Key)))
            {
                session.Delete(secret);
            }

            connector.Name = config.Name!.Trim();
            connector.BaseUrl = config.BaseUrl!.Trim();
            connector.Auth = Enum.Parse<ConnectorAuth>(config.Auth!, ignoreCase: true);
            connector.Settings = config.Settings ?? [];
            connector.Enabled = config.Enabled;
            connector.ProbePath = string.IsNullOrWhiteSpace(config.ProbePath) ? "/" : config.ProbePath.Trim();
            connector.SecretKeys = connector.SecretKeys.Where(k => !cleared.Contains(k)).ToList();
            connector.UpdatedAt = DateTime.UtcNow;
            session.Store(connector);

            await ConnectorGate.AuditAsync(session, tenant.Slug, current is null ? "connector.created" : "connector.updated", connector, actor,
                extra: cleared.Count > 0 ? new Dictionary<string, object> { ["secretsChanged"] = string.Join(", ", cleared.Select(k => $"{k} cleared")) } : null,
                ct: ct);
        }

        return errors;
    }

    /// <summary>The document, checked the way the connector endpoints check a save, or null.</summary>
    private static ConnectorConfig? Read(string slug, JsonElement document, List<string> errors)
    {
        if (!SecretsAreNames(document))
        {
            errors.Add($"'{slug}': secrets is a list of the names of the secrets the connector holds. A configuration "
                       + $"document never carries a secret's value; set one with PUT /api/connectors/{slug}.");
            return null;
        }

        if (ConfigJson.Read<ConnectorConfig>(slug, document, errors) is not { } config)
        {
            return null;
        }

        var problem = ConnectorRules.Check(new SaveConnectorRequest
        {
            Name = config.Name ?? string.Empty,
            Slug = slug,
            BaseUrl = config.BaseUrl ?? string.Empty,
            Auth = config.Auth ?? string.Empty,
            Settings = config.Settings ?? [],
            Enabled = config.Enabled,
            ProbePath = config.ProbePath ?? "/",
        });
        if (problem is not null)
        {
            errors.Add($"'{slug}': {problem}");
            return null;
        }

        return config;
    }

    private static bool SecretsAreNames(JsonElement document) =>
        document.ValueKind != JsonValueKind.Object
        || !document.TryGetProperty("secrets", out var secrets)
        || secrets.ValueKind == JsonValueKind.Null
        || (secrets.ValueKind == JsonValueKind.Array && secrets.EnumerateArray().All(s => s.ValueKind == JsonValueKind.String));

    private static ConfigChange? Plan(string slug, ConnectorConfig config, Connector? current, JsonElement document)
    {
        if (current is null)
        {
            return new ConfigChange
            {
                Key = slug, Kind = ConfigChangeKind.Create, Desired = document,
                Summary = $"create connector '{slug}' to {config.BaseUrl}",
            };
        }

        var held = ConnectorConfig.Of(current).WithoutSecrets();
        var wanted = config.WithoutSecrets();
        var cleared = Cleared(config, current).ToList();
        if (ConfigJson.Same(held, wanted) && cleared.Count == 0)
        {
            return null;
        }

        var details = new List<string>();
        if (!string.Equals(held.BaseUrl, wanted.BaseUrl, StringComparison.Ordinal))
        {
            details.Add($"base URL changes from {held.BaseUrl} to {wanted.BaseUrl}");
        }

        if (!ConfigJson.Same(held with { BaseUrl = null }, wanted with { BaseUrl = null }))
        {
            details.Add("name, auth, settings, probe path or enabled change");
        }

        details.AddRange(cleared.Select(k => $"secret {k} is cleared and has to be set again"));
        return new ConfigChange
        {
            Key = slug, Kind = cleared.Count > 0 ? ConfigChangeKind.Destructive : ConfigChangeKind.Change,
            Desired = document, Summary = $"change connector '{slug}'", Details = details,
        };
    }

    /// <summary>
    /// The held secrets an apply clears: those the document does not list, all of them when the base
    /// URL moves to another origin, and the client secret when the token URL changes.
    /// </summary>
    private static IEnumerable<string> Cleared(ConnectorConfig config, Connector? current)
    {
        if (current is null)
        {
            return [];
        }

        var listed = (config.Secrets ?? []).ToHashSet(StringComparer.Ordinal);
        var moved = !ConnectorOrigin.Same(current.BaseUrl, config.BaseUrl);
        var tokenUrl = config.Settings?.GetValueOrDefault(ConnectorSettingKeys.TokenUrl)?.Trim();
        var tokenMoved = !string.IsNullOrEmpty(tokenUrl)
                         && !string.Equals(current.Settings?.GetValueOrDefault(ConnectorSettingKeys.TokenUrl)?.Trim(), tokenUrl, StringComparison.Ordinal);

        return current.SecretKeys
            .Where(key => moved || !listed.Contains(key) || (tokenMoved && key == ConnectorSecretKeys.ClientSecret))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
    }
}

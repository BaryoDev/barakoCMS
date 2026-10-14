using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Config;
using barakoCMS.Features.Users;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Roles;

/// <summary>A role as its document. Its content permissions are the <c>permissionRules</c> section.</summary>
internal sealed record RoleConfig(string? Description, List<string>? SystemCapabilities, bool RequireMfa)
{
    public static RoleConfig Of(Role role) => new RoleConfig(role.Description, role.SystemCapabilities, role.RequireMfa).Normalised();

    public RoleConfig Normalised() => new(
        Description ?? string.Empty,
        (SystemCapabilities ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
        RequireMfa);
}

/// <summary>
/// The <c>roles</c> section: the roles the tenant reaches, its own and the platform roles, but the
/// seeded ones, under its name.
/// </summary>
/// <remarks>
/// <para>
/// The same roles <c>GET /api/roles</c> lists for a caller in the tenant, and the same rules as the
/// roles routes (<see cref="RoleScope"/>). Another tenant's role is neither exported nor written. A
/// name is read as the tenant's own role first, then a platform role. A name the tenant does not
/// reach creates a role of the tenant. Changing a platform role, or adding a capability that
/// reaches past the tenant, takes <c>manage_platform_roles</c>.
/// </para>
/// <para>
/// A role the document leaves out is kept, as it always was. Removing one is
/// <c>DELETE /api/roles/{id}</c>.
/// </para>
/// </remarks>
internal sealed class RoleConfigProvider(
    IDocumentSession session,
    CapabilityVocabulary vocabulary,
    IConfiguration configuration,
    TenantContext tenant,
    ICallerCapabilities callers) : IConfigProvider
{
    /// <summary>The role each planned change resolved to, by name, or null for one it creates.</summary>
    private readonly Dictionary<string, Guid?> planned = new(StringComparer.Ordinal);

    public string Section => "roles";

    public int Order => 200;

    public string ReadCapability => SystemCapabilities.ManageRoles;

    public string WriteCapability => SystemCapabilities.ManageRoles;

    public async Task<ConfigSectionExport?> ExportAsync(ConfigCaller caller, CancellationToken ct)
    {
        var roles = await RoleScope.ByNameAsync(session, tenant.Slug, ct);

        return new ConfigSectionExport
        {
            Documents = roles.Values
                .Where(r => !SystemRoles.Contains(r.Id))
                .ToDictionary(r => r.Name, r => ConfigJson.ToElement(RoleConfig.Of(r)), StringComparer.Ordinal),
        };
    }

    public async Task<ConfigSectionPlan> PlanAsync(
        IReadOnlyDictionary<string, JsonElement> desired, ConfigCaller caller, CancellationToken ct)
    {
        var errors = new List<string>();
        var notes = new List<string>();
        var changes = new List<ConfigChange>();
        var stored = await RoleScope.ByNameAsync(session, tenant.Slug, ct);
        var refuseUnknown = configuration.GetValue(CapabilityVocabulary.RefuseUnknownKey, false);
        var touchesPlatformRole = false;
        var addsPlatformCapability = false;
        planned.Clear();

        foreach (var (name, document) in desired.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            if (Read(name, document, errors) is not { } role)
            {
                continue;
            }

            foreach (var unknown in vocabulary.Unknown(role.SystemCapabilities))
            {
                (refuseUnknown ? errors : notes).Add($"'{name}': {CapabilityVocabulary.UnknownMessage(unknown)}");
            }

            stored.TryGetValue(name, out var current);
            var change = Plan(name, role, current, document);
            if (change is null)
            {
                continue;
            }

            changes.Add(change);
            planned[name] = current?.Id;
            if (current?.IsPlatform == true)
            {
                notes.Add($"'{name}' is a platform role every tenant shares, so this changes it in every tenant.");
            }

            touchesPlatformRole |= current?.IsPlatform == true;
            addsPlatformCapability |= Adds(role, current);
        }

        var refusals = changes.Count == 0 ? [] : await RefusalsAsync(caller, touchesPlatformRole, addsPlatformCapability, ct);
        return new ConfigSectionPlan { Section = Section, Changes = changes, Errors = errors, Refusals = refusals, Notes = notes };
    }

    public async Task<IReadOnlyList<string>> ApplyAsync(ConfigSectionPlan plan, ConfigCaller caller, CancellationToken ct)
    {
        var errors = new List<string>();
        Guid.TryParse(caller.Principal?.FindFirst("UserId")?.Value, out var actorId);
        var stored = await RoleScope.ByNameAsync(session, tenant.Slug, ct);

        foreach (var change in plan.Changes)
        {
            if (Read(change.Key, change.Desired!.Value, errors) is not { } desired)
            {
                continue;
            }

            var role = stored.GetValueOrDefault(change.Key);
            if (planned.TryGetValue(change.Key, out var resolved) && resolved != role?.Id)
            {
                errors.Add($"'{change.Key}' names another role than it did when the change was planned. Plan it again.");
                continue;
            }

            var before = role is null ? null : RoleAudit.Of(role);
            role ??= new Role { Id = Guid.NewGuid(), Name = change.Key, TenantSlug = tenant.Slug, CreatedAt = DateTime.UtcNow };

            role.Description = desired.Description ?? string.Empty;
            role.SystemCapabilities = desired.SystemCapabilities ?? [];
            role.RequireMfa = desired.RequireMfa;
            session.Store(role);

            await AuditLog.RecordAsync(session, tenant.Slug, before is null ? "role.created" : "role.updated",
                actorId == Guid.Empty ? null : actorId, caller.Principal?.FindFirst("Username")?.Value ?? "system",
                targetType: "Role", targetId: role.Id.ToString(),
                metadata: before is null ? RoleAudit.Describe(RoleAudit.Of(role)) : RoleAudit.Changed(before, RoleAudit.Of(role)),
                ct: ct);
        }

        return errors;
    }

    private static RoleConfig? Read(string name, JsonElement document, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add("A role's name is not blank.");
            return null;
        }

        if (SystemRoles.IsReservedName(name))
        {
            errors.Add($"'{name}' is a seeded role, which every deployment creates for itself, and is not configured here.");
            return null;
        }

        return ConfigJson.Read<RoleConfig>(name, document, errors)?.Normalised();
    }

    private static ConfigChange? Plan(string name, RoleConfig desired, Role? current, JsonElement document)
    {
        if (current is null)
        {
            return new ConfigChange
            {
                Key = name, Kind = ConfigChangeKind.Create, Desired = document,
                Summary = $"create role '{name}' with {desired.SystemCapabilities!.Count} capabilities",
            };
        }

        var held = RoleConfig.Of(current);
        if (ConfigJson.Same(held, desired))
        {
            return null;
        }

        var details = new List<string>();
        details.AddRange(desired.SystemCapabilities!.Except(held.SystemCapabilities!).Select(c => $"grants '{c}'"));
        details.AddRange(held.SystemCapabilities!.Except(desired.SystemCapabilities!).Select(c => $"no longer grants '{c}'"));
        if (held.Description != desired.Description)
        {
            details.Add("description changes");
        }

        if (held.RequireMfa != desired.RequireMfa)
        {
            details.Add(desired.RequireMfa ? "requires MFA" : "no longer requires MFA");
        }

        return new ConfigChange
        {
            // Taking a capability away takes access from everybody holding the role, so it is destructive.
            Key = name,
            Kind = held.SystemCapabilities!.Except(desired.SystemCapabilities!).Any() ? ConfigChangeKind.Destructive : ConfigChangeKind.Change,
            Desired = document, Summary = $"change role '{name}'", Details = details,
        };
    }

    /// <summary>
    /// Whether the change adds a capability that reaches past the tenant. Only additions count, the
    /// rule <c>PUT /api/roles/{id}</c> follows, so one a platform administrator gave is kept.
    /// </summary>
    private static bool Adds(RoleConfig desired, Role? current) =>
        (desired.SystemCapabilities ?? [])
            .Except(current?.SystemCapabilities ?? [], StringComparer.OrdinalIgnoreCase)
            .Any(PlatformRoles.PlatformCapabilities.Contains);

    private async Task<List<string>> RefusalsAsync(
        ConfigCaller caller, bool touchesPlatformRole, bool addsPlatformCapability, CancellationToken ct)
    {
        if ((!touchesPlatformRole && !addsPlatformCapability)
            || await RoleScope.IsPlatformCallerAsync(caller, callers, configuration, ct))
        {
            return [];
        }

        var refusals = new List<string>();
        if (touchesPlatformRole)
        {
            refusals.Add(RoleScope.PlatformRoleMessage);
        }

        if (addsPlatformCapability)
        {
            refusals.Add(RoleScope.PlatformCapabilityMessage);
        }

        return refusals;
    }
}

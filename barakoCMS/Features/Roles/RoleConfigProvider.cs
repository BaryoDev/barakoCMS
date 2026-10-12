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
/// The <c>roles</c> section: every role but the seeded ones, under its name.
/// </summary>
/// <remarks>
/// <para>
/// Roles are stored once for the whole deployment, not per tenant, so this section is the same
/// whichever tenant it is exported from or applied to. That is why writing it takes
/// <c>manage_roles</c> from one of the caller's global roles, the rule <see cref="PlatformScope"/>
/// applies to every document stored once: an administrator of one tenant holds Admin there through
/// a membership and must not reach every tenant's roles that way. A role carrying a platform
/// capability, before or after the change, is written only by a SuperAdmin, the rule both role
/// surfaces apply, so an apply cannot grant what the caller could not grant through them.
/// </para>
/// <para>
/// A role the document leaves out is kept: a role is shared by every tenant, and one tenant's file
/// cannot know every other tenant's roles. Removing one is <c>DELETE /api/roles/{id}</c>.
/// </para>
/// </remarks>
internal sealed class RoleConfigProvider(
    IDocumentSession session,
    CapabilityVocabulary vocabulary,
    IConfiguration configuration,
    TenantContext tenant) : IConfigProvider
{
    public string Section => "roles";

    public int Order => 200;

    public string ReadCapability => SystemCapabilities.ManageRoles;

    public string WriteCapability => SystemCapabilities.ManageRoles;

    public async Task<ConfigSectionExport?> ExportAsync(ConfigCaller caller, CancellationToken ct)
    {
        var roles = await session.Query<Role>().ToListAsync(ct);

        return new ConfigSectionExport
        {
            Documents = roles
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
        var stored = (await session.Query<Role>().ToListAsync(ct)).ToDictionary(r => r.Name, StringComparer.Ordinal);
        var refuseUnknown = configuration.GetValue(CapabilityVocabulary.RefuseUnknownKey, false);
        var platform = false;

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
            platform |= Carries(role) || (current is not null && PlatformRoles.CarriesPlatformCapability(current));
        }

        var refusals = changes.Count == 0 ? [] : await RefusalsAsync(caller, platform, ct);
        return new ConfigSectionPlan { Section = Section, Changes = changes, Errors = errors, Refusals = refusals, Notes = notes };
    }

    public async Task<IReadOnlyList<string>> ApplyAsync(ConfigSectionPlan plan, ConfigCaller caller, CancellationToken ct)
    {
        var errors = new List<string>();
        Guid.TryParse(caller.Principal?.FindFirst("UserId")?.Value, out var actorId);

        foreach (var change in plan.Changes)
        {
            if (Read(change.Key, change.Desired!.Value, errors) is not { } desired)
            {
                continue;
            }

            var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == change.Key, ct);
            var before = role is null ? null : RoleAudit.Of(role);
            role ??= new Role { Id = Guid.NewGuid(), Name = change.Key, CreatedAt = DateTime.UtcNow };

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

    private static bool Carries(RoleConfig role) =>
        (role.SystemCapabilities ?? []).Any(PlatformRoles.PlatformCapabilities.Contains);

    private async Task<List<string>> RefusalsAsync(ConfigCaller caller, bool platform, CancellationToken ct)
    {
        if (caller.Principal is not { } principal)
        {
            return [];
        }

        if (!await PlatformScope.HoldsGloballyAsync(session, principal, configuration, SystemCapabilities.ManageRoles, [], ct))
        {
            return ["Roles are shared by every tenant, so writing one takes manage_roles from one of your global "
                    + "roles, not from a tenant membership."];
        }

        return platform && !await PlatformRoles.IsSuperAdminAsync(session, principal, ct)
            ? [PlatformRoles.PlatformRoleRefusedMessage]
            : [];
    }
}

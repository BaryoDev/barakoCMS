using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Config;
using barakoCMS.Features.Users;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Roles;

/// <summary>
/// The <c>permissionRules</c> section: what each role may do with each content type of the tenant,
/// under <c>{role}/{contentType}</c>.
/// </summary>
/// <remarks>
/// <para>
/// A rule names a content type, and a content type belongs to one tenant, so this section holds the
/// rules for the tenant's own types and nothing else. A rule a role holds for a type the tenant does
/// not have is another tenant's business: it is neither exported nor removed from here. A rule the
/// document leaves out, for one of the tenant's types, is removed destructively.
/// </para>
/// <para>
/// Rules live on the role, so the roles are the ones the tenant reaches, read by name the way the
/// <c>roles</c> section reads them (<see cref="RoleScope"/>): its own, then the platform roles.
/// Another tenant's role is out of reach. Writing a rule on a platform role takes
/// <c>manage_platform_roles</c>, the rule <c>PUT /api/roles/{id}</c> applies, and since every tenant
/// shares a platform role, removing one of its rules removes it for any other tenant with a type of
/// that name: the plan says which in a note, to a caller allowed to write the rule and to nobody
/// else. The checks <c>PUT /api/roles/{id}</c> runs on a role's rules run here on the rules the role
/// would hold.
/// </para>
/// </remarks>
internal sealed class PermissionRuleConfigProvider(
    IDocumentSession session,
    IDocumentStore store,
    IConfiguration configuration,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant,
    ICallerCapabilities callers) : IConfigProvider
{
    public string Section => "permissionRules";

    public int Order => 300;

    public string ReadCapability => SystemCapabilities.ManageRoles;

    public string WriteCapability => SystemCapabilities.ManageRoles;

    public async Task<ConfigSectionExport?> ExportAsync(ConfigCaller caller, CancellationToken ct)
    {
        var (roles, types) = await ReadAsync(ct);
        var documents = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var role in roles)
        {
            foreach (var permission in Managed(role, types))
            {
                documents[Key(role.Name, permission.ContentTypeSlug)] = Body(permission);
            }
        }

        return new ConfigSectionExport { Documents = documents };
    }

    public async Task<ConfigSectionPlan> PlanAsync(
        IReadOnlyDictionary<string, JsonElement> desired, ConfigCaller caller, CancellationToken ct)
    {
        var errors = new List<string>();
        var changes = new List<ConfigChange>();
        var (roles, types) = await ReadAsync(ct);
        var wanted = Wanted(desired, roles, types, errors);
        var platform = false;

        foreach (var role in roles)
        {
            var held = Managed(role, types).ToDictionary(p => barakoCMS.Core.ContentTypeName.Normalize(p.ContentTypeSlug), StringComparer.Ordinal);
            var mine = wanted.GetValueOrDefault(role.Name) ?? [];
            var before = changes.Count;

            foreach (var (slug, (key, permission, document)) in mine.OrderBy(m => m.Key, StringComparer.Ordinal))
            {
                if (!held.TryGetValue(slug, out var current))
                {
                    changes.Add(new ConfigChange { Key = key, Kind = ConfigChangeKind.Create, Desired = document, Summary = $"give role '{role.Name}' rules on '{slug}'" });
                }
                else if (!JsonElement.DeepEquals(Body(current), Body(permission)))
                {
                    changes.Add(new ConfigChange { Key = key, Kind = ConfigChangeKind.Change, Desired = document, Summary = $"change role '{role.Name}' rules on '{slug}'" });
                }
            }

            changes.AddRange(held.Where(h => !mine.ContainsKey(h.Key)).OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => new ConfigChange
            {
                Key = Key(role.Name, h.Value.ContentTypeSlug), Kind = ConfigChangeKind.Destructive,
                Summary = $"remove role '{role.Name}' rules on '{h.Key}', taking away what they grant",
            }));

            if (changes.Count > before)
            {
                platform |= role.IsPlatform;
                errors.AddRange((await CheckAsync(role, Next(role, types, mine), ct)).Select(e => $"role '{role.Name}': {e}"));
            }
        }

        var refusals = changes.Count == 0 ? [] : await RefusalsAsync(caller, platform, ct);

        // Names other tenants, so only for a caller who may write the shared roles anyway. Anyone
        // else could stage a type name and read back which tenants have it.
        var platformNames = roles.Where(r => r.IsPlatform).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var notes = refusals.Count == 0
            ? await SharedRemovalsAsync(changes.Where(c => platformNames.Contains(SplitKey(c.Key).Role)).ToList(), ct)
            : [];
        return new ConfigSectionPlan { Section = Section, Changes = changes, Errors = errors, Refusals = refusals, Notes = notes };
    }

    /// <summary>
    /// A note for each removed rule on a platform role whose type name another tenant also has. The
    /// role is shared and a rule names a type by its name, so removing one here removes it there too.
    /// </summary>
    private async Task<List<string>> SharedRemovalsAsync(List<ConfigChange> changes, CancellationToken ct)
    {
        var removed = changes.Where(c => c.Desired is null)
            .Select(c => (c.Key, Slug: barakoCMS.Core.ContentTypeName.Normalize(SplitKey(c.Key).Slug)))
            .ToList();
        if (removed.Count == 0)
        {
            return [];
        }

        var holders = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var slug in (await session.Query<Tenant>().Select(t => t.Slug).ToListAsync(ct)).Append(Tenant.DefaultSlug).Distinct())
        {
            if (slug == tenant.Slug)
            {
                continue;
            }

            await using var other = slug == Tenant.DefaultSlug ? store.QuerySession() : store.QuerySession(slug);
            foreach (var name in (await other.Query<ContentTypeDefinition>().Select(t => t.Name).ToListAsync(ct)).Select(barakoCMS.Core.ContentTypeName.Normalize))
            {
                if (removed.Any(r => r.Slug == name))
                {
                    (holders.TryGetValue(name, out var list) ? list : holders[name] = []).Add(slug);
                }
            }
        }

        return removed.Where(r => holders.ContainsKey(r.Slug))
            .Select(r => $"Removing '{r.Key}' removes it for {string.Join(", ", holders[r.Slug])} too, which "
                         + $"{(holders[r.Slug].Count == 1 ? "has" : "have")} a type named '{r.Slug}': roles are shared, and a rule names a type by its name.")
            .ToList();
    }

    public async Task<IReadOnlyList<string>> ApplyAsync(ConfigSectionPlan plan, ConfigCaller caller, CancellationToken ct)
    {
        var errors = new List<string>();
        var (roles, types) = await ReadAsync(ct);
        var desired = plan.Changes.Where(c => c.Desired is not null).ToDictionary(c => c.Key, c => c.Desired!.Value, StringComparer.Ordinal);
        var removed = plan.Changes.Where(c => c.Desired is null).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var wanted = Wanted(desired, roles, types, errors);

        foreach (var role in roles)
        {
            var touched = wanted.ContainsKey(role.Name) || removed.Any(k => SplitKey(k).Role == role.Name);
            if (!touched)
            {
                continue;
            }

            var mine = Managed(role, types)
                .Where(p => !removed.Contains(Key(role.Name, p.ContentTypeSlug)))
                .ToDictionary(p => barakoCMS.Core.ContentTypeName.Normalize(p.ContentTypeSlug), p => (Key(role.Name, p.ContentTypeSlug), p, default(JsonElement)), StringComparer.Ordinal);
            foreach (var (slug, entry) in wanted.GetValueOrDefault(role.Name) ?? [])
            {
                mine[slug] = entry;
            }

            var before = barakoCMS.Infrastructure.Audit.RoleAudit.Of(role);
            role.Permissions = Next(role, types, mine);
            session.Store(role);

            // The entry PUT /api/roles/{id} writes for the same change, staged with it.
            await barakoCMS.Infrastructure.Audit.AuditLog.RecordAsync(session, tenant.Slug, "role.updated",
                Guid.TryParse(caller.Principal?.FindFirst("UserId")?.Value, out var actorId) ? actorId : null,
                caller.Principal?.FindFirst("Username")?.Value ?? "system",
                targetType: "Role", targetId: role.Id.ToString(),
                metadata: barakoCMS.Infrastructure.Audit.RoleAudit.Changed(before, barakoCMS.Infrastructure.Audit.RoleAudit.Of(role)), ct: ct);
        }

        return errors;
    }

    private async Task<(List<Role> Roles, HashSet<string> Types)> ReadAsync(CancellationToken ct)
    {
        var roles = (await RoleScope.ByNameAsync(session, tenant.Slug, ct)).Values.Where(r => r.Id != SystemRoles.SuperAdminRoleId).ToList();
        var types = (await session.Query<ContentTypeDefinition>().Select(t => t.Name).ToListAsync(ct))
            .Select(barakoCMS.Core.ContentTypeName.Normalize)
            .ToHashSet(StringComparer.Ordinal);
        return (roles, types);
    }

    /// <summary>The rules a role holds for the tenant's own types.</summary>
    private static IEnumerable<ContentTypePermission> Managed(Role role, HashSet<string> types) =>
        (role.Permissions ?? []).Where(p => p is not null && types.Contains(barakoCMS.Core.ContentTypeName.Normalize(p.ContentTypeSlug)));

    /// <summary>The document's rules by role and type, each read and named by its normalised type.</summary>
    private static Dictionary<string, Dictionary<string, (string Key, ContentTypePermission Permission, JsonElement Document)>> Wanted(
        IReadOnlyDictionary<string, JsonElement> desired, List<Role> roles, HashSet<string> types, List<string> errors)
    {
        var wanted = new Dictionary<string, Dictionary<string, (string, ContentTypePermission, JsonElement)>>(StringComparer.Ordinal);

        foreach (var (key, document) in desired)
        {
            var (roleName, slug) = SplitKey(key);
            var normalized = barakoCMS.Core.ContentTypeName.Normalize(slug);
            if (roleName.Length == 0 || slug.Length == 0)
            {
                errors.Add($"'{key}' is not a rule key, which is the role's name and the content type's, as role/type.");
                continue;
            }

            if (!roles.Any(r => r.Name == roleName))
            {
                errors.Add($"'{key}': there is no role '{roleName}' to hold it, here or earlier in the document.");
                continue;
            }

            if (!types.Contains(normalized))
            {
                errors.Add($"'{key}': this tenant has no content type '{slug}'.");
                continue;
            }

            if (ConfigJson.Read<ContentTypePermission>(key, document, errors) is not { } permission)
            {
                continue;
            }

            permission.ContentTypeSlug = normalized;
            var mine = wanted.TryGetValue(roleName, out var found) ? found : wanted[roleName] = new(StringComparer.Ordinal);
            if (!mine.TryAdd(normalized, (key, permission, document)))
            {
                errors.Add($"'{key}': role '{roleName}' already has rules on '{normalized}' in this document.");
            }
        }

        return wanted;
    }

    /// <summary>The rules the role would hold: those for other tenants' types, then this tenant's as wanted.</summary>
    private static List<ContentTypePermission> Next(
        Role role, HashSet<string> types, Dictionary<string, (string Key, ContentTypePermission Permission, JsonElement Document)> mine)
    {
        var next = (role.Permissions ?? []).Where(p => p is not null && !types.Contains(barakoCMS.Core.ContentTypeName.Normalize(p.ContentTypeSlug))).ToList();
        next.AddRange(mine.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => m.Value.Permission));
        FieldSetRules.Normalise(next);
        return next;
    }

    private async Task<List<string>> CheckAsync(Role role, List<ContentTypePermission> next, CancellationToken ct)
    {
        var errors = ReferenceConditionRules.ShapeErrors(next).Concat(FieldSetRules.ShapeErrors(next)).ToList();
        if (errors.Count > 0)
        {
            return errors;
        }

        errors.AddRange(await ReferenceConditionRules.CheckAsync(session, next, role.Permissions, ct));
        errors.AddRange(await FieldSetRules.CheckAsync(session, next, role.Permissions, ct));
        return errors;
    }

    private async Task<List<string>> RefusalsAsync(ConfigCaller caller, bool platform, CancellationToken ct) =>
        platform && !await RoleScope.IsPlatformCallerAsync(caller, callers, configuration, ct)
            ? [RoleScope.PlatformRoleMessage]
            : [];

    private static string Key(string role, string slug) => $"{role}/{slug}";

    /// <summary>The role and the type of a key. Split at the last '/', since a type name holds none.</summary>
    private static (string Role, string Slug) SplitKey(string key)
    {
        var at = key.LastIndexOf('/');
        return at <= 0 ? (string.Empty, string.Empty) : (key[..at], key[(at + 1)..]);
    }

    /// <summary>A rule as its document: everything but the type it names, which is in the key.</summary>
    private static JsonElement Body(ContentTypePermission permission)
    {
        var copy = JsonSerializer.Deserialize<ContentTypePermission>(ConfigJson.ToElement(permission), ConfigJson.Options)!;
        FieldSetRules.Normalise([copy]);
        var node = JsonSerializer.SerializeToNode(copy, ConfigJson.Options)!.AsObject();
        node.Remove("contentTypeSlug");
        return JsonSerializer.SerializeToElement(node, ConfigJson.Options);
    }
}

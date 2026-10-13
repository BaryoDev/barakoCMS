using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Config;
using barakoCMS.Features.Tenants;
using barakoCMS.Features.Tenants.Members;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Hosting;

namespace barakoCMS.Data;

/// <summary>A tenant as the seed file declares it.</summary>
internal sealed record SeedTenant(string? Name, bool IsActive = true, List<string>? Domains = null, List<SeedMember>? Members = null, JsonElement? Config = null);

/// <summary>A person the seed file adds to a tenant, by email, with the roles they hold there by name.</summary>
internal sealed record SeedMember(string? Email, List<string>? Roles);

/// <summary>
/// The file <c>Seed:File</c> names, applied when the server starts, so a new instance is one reviewed
/// file rather than a run of API calls (#905).
/// </summary>
/// <remarks>
/// <para>
/// The file is a config document, the format <c>GET /api/config/export</c> writes, plus a
/// <c>tenants</c> section only this reads: each tenant by handle, with its name, domains, first
/// members and, under <c>config</c>, the sections to apply inside it. The top-level sections apply
/// to the default tenant. Everything goes through the engine the API uses, as the host itself. A
/// destructive change and a section written for a signed-in caller are skipped, each named in a
/// warning, and the rest is applied.
/// </para>
/// <para>
/// Applied once per file. What was applied is recorded by its SHA-256, and a start that finds the
/// same file again reads nothing else and changes nothing, so a change made through the API since is
/// not undone at the next restart. A changed file is applied again, every section it names, which
/// reverts a non-destructive API edit in those sections.
/// </para>
/// <para>
/// A file that cannot be applied stops the seed with every reason, and its hash is not recorded.
/// What was applied before the refusal stays: the default tenant's sections and each tenant before
/// the one refused commit as they go. The next start applies the file again from the top, and the
/// apply converges, so fixing the file and restarting finishes the job.
/// </para>
/// </remarks>
internal static class SeedFile
{
    public const string Key = "Seed:File";

    /// <summary>The system setting holding the SHA-256 of the file last applied.</summary>
    public const string AppliedKey = "Seed:File:Sha256";

    public static async Task ApplyAsync(IServiceProvider services, IConfiguration configuration, IHostEnvironment environment, CancellationToken ct)
    {
        var configured = configuration[Key];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return;
        }

        var path = Path.GetFullPath(configured, environment.ContentRootPath);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"{Key} names {path}, and there is no file there.");
        }

        var bytes = await File.ReadAllBytesAsync(path, ct);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        using var scope = services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var applied = await session.Query<SystemSetting>().FirstOrDefaultAsync(s => s.Key == AppliedKey, ct);
        if (applied?.Value == hash)
        {
            Console.WriteLine($"[DataSeeder] {Key} was applied already, unchanged since; nothing to do.");
            return;
        }

        var root = Parse(bytes);
        var tenants = ReadTenants(root);
        Validate(tenants);

        await ApplyConfigAsync(services, null, Without(root, ConfigDocument.TenantsSection), ct);
        foreach (var (slug, tenant) in tenants)
        {
            await ApplyTenantAsync(services, session, slug, tenant, root, ct);
        }

        applied ??= new SystemSetting { Id = Guid.NewGuid(), Key = AppliedKey, Category = SettingCategory.System };
        applied.Value = hash;
        applied.Description = $"The SHA-256 of the {Key} the seeder last applied.";
        applied.UpdatedAt = DateTime.UtcNow;
        session.Store(applied);
        await session.SaveChangesAsync(ct);
        Console.WriteLine($"[DataSeeder] Applied {Key} ({tenants.Count} tenant(s)).");
    }

    private static JsonObject Parse(byte[] bytes)
    {
        try
        {
            return JsonNode.Parse(bytes) as JsonObject
                   ?? throw new InvalidOperationException($"{Key} must hold a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{Key} is not valid JSON: {ex.Message}", ex);
        }
    }

    private static Dictionary<string, SeedTenant> ReadTenants(JsonObject root)
    {
        if (root[ConfigDocument.TenantsSection] is not { } node)
        {
            return [];
        }

        try
        {
            return node.Deserialize<Dictionary<string, SeedTenant>>(ConfigJson.Options) ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{Key}: tenants cannot be read: {ex.Message}", ex);
        }
    }

    /// <summary>What can be refused before anything is written, so a bad handle does not leave half an instance.</summary>
    private static void Validate(Dictionary<string, SeedTenant> tenants)
    {
        var problems = new List<string>();
        foreach (var (slug, tenant) in tenants)
        {
            if (!TenantHandles.IsValidHandle(slug))
            {
                problems.Add($"'{slug}' is not a tenant handle (3-40 characters of a-z, 0-9 and hyphens, not reserved).");
            }

            TenantDomains.Normalise(tenant.Domains ?? [], out var errors);
            problems.AddRange(errors.Select(e => $"tenant '{slug}': {e}"));
            problems.AddRange((tenant.Members ?? [])
                .Where(m => !(m.Email ?? string.Empty).Contains('@'))
                .Select(_ => $"tenant '{slug}' has a member with no valid email address."));
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException($"{Key} cannot be applied: {string.Join(" | ", problems)}");
        }
    }

    private static JsonObject Without(JsonObject root, string section)
    {
        var copy = root.DeepClone().AsObject();
        copy.Remove(section);
        return copy;
    }

    /// <summary>Applies one document in one tenant, or the default tenant for a null slug, as the host.</summary>
    private static async Task ApplyConfigAsync(IServiceProvider services, string? slug, JsonObject document, CancellationToken ct)
    {
        if (!document.Any(p => !ConfigDocument.MetadataKeys.Contains(p.Key)))
        {
            return;
        }

        using var scope = services.CreateScopeForTenant(slug);
        var engine = scope.ServiceProvider.GetRequiredService<ConfigEngine>();
        var (result, skipped) = await engine.ApplyAsHostAsync(JsonSerializer.SerializeToElement(document), ct);
        foreach (var line in skipped)
        {
            Console.WriteLine($"[DataSeeder] WARNING: {Key}, {slug ?? Tenant.DefaultSlug} tenant, skipped {line}.");
        }

        if (!result.Response.Applied)
        {
            // A skipped change is the whole document it belongs to, so a later section relying on
            // any part of it fails here, and editing that section alone does not fix it.
            var hint = result.Response.Sections.Any(s => s.Notes.Any(n => n.StartsWith(ConfigEngine.SkippedPrefix, StringComparison.Ordinal)))
                ? " A destructive change was skipped in this tenant (logged above), and a section that relies on what it would have "
                  + "changed is a likely cause. Make it with POST /api/config/apply and allowDestructive, then restart."
                : string.Empty;
            throw new InvalidOperationException(
                $"{Key} could not be applied to the {slug ?? Tenant.DefaultSlug} tenant: {Describe(result.Response)}{hint}");
        }
    }

    private static string Describe(ConfigPlanResponse response)
    {
        var reasons = response.Errors
            .Concat(response.Refusals)
            .Concat(response.Sections.SelectMany(s => s.Errors.Concat(s.Refusals).Select(e => $"{s.Section}: {e}")))
            .ToList();
        return string.Join(" | ", reasons);
    }

    private static async Task ApplyTenantAsync(
        IServiceProvider services, IDocumentSession session, string slug, SeedTenant declared, JsonObject root, CancellationToken ct)
    {
        await UpsertTenantAsync(services, session, slug, declared, ct);

        if (declared.Config is { ValueKind: JsonValueKind.Object } config)
        {
            var document = JsonNode.Parse(config.GetRawText())!.AsObject();
            document["version"] ??= root["version"]?.DeepClone();
            await ApplyConfigAsync(services, slug, document, ct);
        }

        foreach (var member in declared.Members ?? [])
        {
            await AddMemberAsync(session, slug, member, ct);
        }

        await session.SaveChangesAsync(ct);
    }

    /// <summary>Creates the tenant, or brings its name, state and domains to what the file says.</summary>
    private static async Task UpsertTenantAsync(
        IServiceProvider services, IDocumentSession session, string slug, SeedTenant declared, CancellationToken ct)
    {
        if (!TenantHandles.IsValidHandle(slug))
        {
            throw new InvalidOperationException($"{Key}: '{slug}' is not a tenant handle (3-40 characters of a-z, 0-9 and hyphens, not reserved).");
        }

        var domains = TenantDomains.Normalise(declared.Domains ?? [], out var errors);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"{Key}: tenant '{slug}': {string.Join(" ", errors)}");
        }

        if (await TenantDomainClash.FindAsync(session, domains, exceptSlug: slug, ct) is { } clash)
        {
            throw new InvalidOperationException($"{Key}: '{clash.Domain}' is already a domain of tenant '{clash.Slug}'.");
        }

        var tenant = await session.Query<Tenant>().FirstOrDefaultAsync(t => t.Slug == slug, ct)
                     ?? new Tenant { Id = Guid.NewGuid(), Slug = slug, CreatedAt = DateTime.UtcNow };
        var name = string.IsNullOrWhiteSpace(declared.Name) ? slug : declared.Name.Trim();

        // isActive false suspends an active tenant and leaves an archived one archived.
        var status = declared.IsActive ? TenantStatus.Active
            : tenant.Status == TenantStatus.Active ? TenantStatus.Suspended : tenant.Status;
        if (tenant.Name != name || tenant.Status != status || !tenant.Domains.SequenceEqual(domains))
        {
            tenant.Name = name;
            tenant.Status = status;
            tenant.Domains = domains.ToList();
            session.Store(tenant);
            await session.SaveChangesAsync(ct);
            services.GetRequiredService<ITenantDomainSource>().Invalidate();
        }
    }

    /// <summary>
    /// Adds a person by email the way <c>POST /api/tenants/members</c> does: a user with no password
    /// when the address is new, who signs in with an emailed code, and a membership holding the
    /// roles named.
    /// </summary>
    private static async Task AddMemberAsync(IDocumentSession session, string slug, SeedMember member, CancellationToken ct)
    {
        var email = (member.Email ?? string.Empty).Trim().ToLowerInvariant();
        if (!email.Contains('@'))
        {
            throw new InvalidOperationException($"{Key}: tenant '{slug}' has a member with no valid email address.");
        }

        var roleIds = await RoleIdsAsync(session, slug, email, member.Roles ?? [], ct);
        var user = await session.Query<User>().FirstOrDefaultAsync(u => u.NormalizedEmail == email, ct);
        if (user is null)
        {
            var taken = await session.Query<User>().AnyAsync(u => u.NormalizedUsername == email, ct);
            user = new User
            {
                Id = Guid.NewGuid(),
                Email = email,
                Username = taken ? $"{email}+{Guid.NewGuid():N}"[..(email.Length + 9)] : email,
                PasswordHash = string.Empty,
                RoleIds = [],
            };
            session.Store(user);
        }

        await SeedMemberships.GrantAsync(session, slug, user.Id, roleIds, ct);
    }

    private static async Task<List<Guid>> RoleIdsAsync(IDocumentSession session, string slug, string email, List<string> names, CancellationToken ct)
    {
        // Among the roles the tenant reaches, its own first, so a name another tenant also uses is not theirs.
        var reachable = await barakoCMS.Features.Roles.RoleScope.ByNameAsync(session, slug, ct);
        var roles = names.Distinct(StringComparer.Ordinal).Where(reachable.ContainsKey).Select(n => reachable[n]).ToList();
        var missing = names.Except(roles.Select(r => r.Name), StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"{Key}: {email} in '{slug}' names roles that do not exist: {string.Join(", ", missing)}.");
        }

        if (roles.Any(r => !Members.IsAssignable(r.Id)))
        {
            throw new InvalidOperationException($"{Key}: SuperAdmin is a platform role and is not granted inside a tenant.");
        }

        return roles.Select(r => r.Id).Distinct().ToList();
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Features.Audit.List;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Audit;

/// <summary>
/// A tenant and callers inside it, minted the way <c>TokenIssuer</c> mints them: the token carries
/// the tenant claim, so each request goes through <c>TenantAccessMiddleware</c> as deployed.
/// </summary>
internal static class AuditTenants
{
    private static int _ipCounter;

    public static async Task<string> CreateAsync(IntegrationTestFixture factory)
    {
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"aud-{Guid.NewGuid():N}"[..16];
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
        await session.SaveChangesAsync();
        return slug;
    }

    /// <summary>A user holding <paramref name="roleIds"/> through a membership of the tenant.</summary>
    public static async Task<Guid> MemberAsync(
        IntegrationTestFixture factory, string slug, MembershipStatus status, params Guid[] roleIds)
    {
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        session.Store(new User
        {
            Id = id,
            Username = $"aud-{id:N}",
            Email = $"aud-{id:N}@example.com",
            PasswordHash = string.Empty,
        });
        session.Store(new Membership
        {
            Id = Guid.NewGuid(),
            UserId = id,
            TenantSlug = slug,
            Status = status,
            RoleIds = roleIds.ToList(),
        });
        await session.SaveChangesAsync();
        return id;
    }

    /// <summary>An administrator of the tenant, and the id the rows will name as the actor.</summary>
    public static async Task<(HttpClient Client, Guid UserId)> AdminAsync(IntegrationTestFixture factory, string slug)
    {
        var userId = await MemberAsync(factory, slug, MembershipStatus.Active, SystemRoles.AdminRoleId);
        return (ClientFor(factory, slug, userId, "Admin"), userId);
    }

    /// <summary>
    /// A member of the tenant holding exactly <paramref name="capabilities"/>, through a role made
    /// for this caller. The role name is not one any gate honours, so the capabilities are all the
    /// caller has.
    /// </summary>
    public static async Task<HttpClient> HolderOfAsync(
        IntegrationTestFixture factory, string slug, params string[] capabilities)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Audit caller {Guid.NewGuid():N}",
            SystemCapabilities = capabilities.ToList(),
        };

        using (var scope = factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(role);
            await session.SaveChangesAsync();
        }

        var userId = await MemberAsync(factory, slug, MembershipStatus.Active, role.Id);
        return ClientFor(factory, slug, userId, role.Name);
    }

    private static HttpClient ClientFor(IntegrationTestFixture factory, string slug, Guid userId, string roleName)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", factory.CreateToken(
                roles: [roleName],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = slug }));
        client.DefaultRequestHeaders.Add("X-Tenant", slug);
        client.DefaultRequestHeaders.Add(
            TestRemoteIpFilter.Header, $"198.51.100.{Interlocked.Increment(ref _ipCounter) % 250 + 1}");
        return client;
    }
}

/// <summary>
/// Reads audit rows, as stored and as <c>GET /api/audit</c> returns them, for the tests that assert
/// what a grant change recorded and who may read it.
/// </summary>
internal static class AuditRows
{
    public static async Task<List<AuditEvent>> ForTargetAsync(
        IntegrationTestFixture factory, string action, string targetId)
    {
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var rows = await session.Query<AuditEvent>()
            .Where(e => e.Action == action && e.TargetId == targetId)
            .ToListAsync();
        return rows.ToList();
    }

    /// <summary>The rows the list endpoint gives this caller for one action and one target.</summary>
    public static async Task<List<AuditEventDto>> ListedAsync(HttpClient client, string action, string targetId)
    {
        var response = await client.GetAsync($"/api/audit?action={action}&pageSize=100");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var page = (await response.Content.ReadFromJsonAsync<PaginatedResponse<AuditEventDto>>())!;
        return page.Items.Where(i => i.TargetId == targetId).ToList();
    }

    /// <summary>
    /// One metadata value as JSON. Metadata is <c>Dictionary&lt;string, object&gt;</c>, so what comes
    /// back from the store is not the type that was written.
    /// </summary>
    public static JsonElement Element(this AuditEvent row, string key) =>
        JsonSerializer.SerializeToElement(row.Metadata![key]);

    /// <summary>A metadata list of strings.</summary>
    public static List<string> Strings(this AuditEvent row, string key) =>
        row.Element(key).EnumerateArray().Select(e => e.GetString()!).ToList();

    /// <summary>The items of a capped list of strings, after asserting it was not truncated.</summary>
    public static List<string> Items(this AuditEvent row, string key)
    {
        var capped = row.Element(key);
        capped.GetProperty("truncated").GetBoolean().Should().BeFalse();
        var items = capped.GetProperty("items").EnumerateArray().Select(e => e.GetString()!).ToList();
        capped.GetProperty("count").GetInt32().Should().Be(items.Count);
        return items;
    }

    public static string Text(this AuditEvent row, string key) => row.Metadata![key].ToString()!;

    /// <summary>The whole row as JSON, for asserting that a value appears nowhere in it.</summary>
    public static string Json(this AuditEvent row) => JsonSerializer.Serialize(row);
}

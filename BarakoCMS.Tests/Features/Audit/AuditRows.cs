using System.Text.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
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

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", factory.CreateToken(
                roles: ["Admin"],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = slug }));
        client.DefaultRequestHeaders.Add("X-Tenant", slug);
        client.DefaultRequestHeaders.Add(
            TestRemoteIpFilter.Header, $"198.51.100.{Interlocked.Increment(ref _ipCounter) % 250 + 1}");
        return (client, userId);
    }
}

/// <summary>
/// Reads audit rows as they were stored, for the tests that assert what a grant change recorded.
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

    /// <summary>
    /// A metadata list as strings. Metadata is <c>Dictionary&lt;string, object&gt;</c>, so a stored
    /// list comes back as a JSON element, not as the list that was written.
    /// </summary>
    public static List<string> Strings(this AuditEvent row, string key) =>
        JsonSerializer.Deserialize<List<string>>(JsonSerializer.Serialize(row.Metadata![key]))!;

    public static string Text(this AuditEvent row, string key) => row.Metadata![key].ToString()!;

    /// <summary>The whole row as JSON, for asserting that a value appears nowhere in it.</summary>
    public static string Json(this AuditEvent row) => JsonSerializer.Serialize(row);
}

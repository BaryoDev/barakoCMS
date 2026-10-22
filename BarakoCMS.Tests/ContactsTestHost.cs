using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.Contacts;
using FluentAssertions;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests;

/// <summary>
/// What the Contacts test classes share: a tenant, a caller in it, and the content type a tenant
/// keeps for its contacts.
/// </summary>
internal sealed class ContactsTestHost(IntegrationTestFixture factory)
{
    private static int _ip;

    public async Task<string> TenantAsync()
    {
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"ct-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
        await session.SaveChangesAsync();
        return slug;
    }

    /// <summary>A signed-in member of the tenant holding the named roles, in a rate-limit bucket of its own.</summary>
    public async Task<HttpClient> MemberAsync(string tenant, params string[] roleNames)
    {
        var userId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var roleIds = new List<Guid>();
            foreach (var name in roleNames)
            {
                roleIds.Add((await session.Query<Role>().FirstAsync(r => r.Name == name)).Id);
            }

            session.Store(new User
            {
                Id = userId,
                Username = $"ct-{Guid.NewGuid():n}"[..14],
                Email = $"ct-{Guid.NewGuid():n}@example.com",
                RoleIds = roleIds,
            });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TenantSlug = tenant,
                Status = MembershipStatus.Active,
                RoleIds = roleIds,
            });
            await session.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", factory.CreateToken(
                roles: roleNames,
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = tenant }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        return client;
    }

    public Task<HttpClient> AdminAsync(string tenant) => MemberAsync(tenant, "SuperAdmin", "Admin");

    /// <summary>Someone who is signed in and holds neither contacts capability.</summary>
    public Task<HttpClient> OutsiderAsync(string tenant) => MemberAsync(tenant, "User");

    public HttpClient Anonymous(string tenant)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        return client;
    }

    /// <summary>
    /// Stores the tenant's own copy of the contact-record type: the built-in fields, plus the extras
    /// named, with delivery switched as asked.
    /// </summary>
    public async Task DeclareTypeAsync(
        string tenant, bool deliverable, Action<ContentTypeDefinition>? shape = null)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenant);

        var definition = ContactRecord.Definition();
        definition.Id = Guid.NewGuid();
        definition.IsPubliclyDeliverable = deliverable;
        shape?.Invoke(definition);
        session.Store(definition);
        await session.SaveChangesAsync();
    }

    public static Task<HttpResponseMessage> CreateAsync(
        HttpClient client, string name, string? email = null, string kind = "Person",
        Dictionary<string, object>? fields = null) =>
        client.PostAsJsonAsync("/api/contacts", new
        {
            kind,
            displayName = name,
            email,
            phone = email is null ? null : "+63 900 000 0000",
            address = email is null ? null : "1 Sample Street",
            fields,
        });

    public static async Task<Guid> CreatedIdAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    public static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    public static string UniqueEmail() => $"{Guid.NewGuid():n}@example.com";

    private static string NextIp() => $"198.51.100.{Interlocked.Increment(ref _ip) % 250 + 1}";
}

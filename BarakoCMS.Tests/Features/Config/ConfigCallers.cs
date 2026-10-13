using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Config;

/// <summary>Tenants and callers for the config engine tests, and the three calls they make.</summary>
internal sealed class ConfigCallers(IntegrationTestFixture fixture)
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async Task<string> TenantAsync()
    {
        var slug = $"cfg-{Guid.NewGuid():n}"[..16];
        await using var session = fixture.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, Status = TenantStatus.Active });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    /// <summary>A role holding exactly these capabilities, under a name nobody else uses.</summary>
    public async Task<Guid> RoleAsync(params string[] capabilities)
    {
        var role = new Role { Id = Guid.NewGuid(), Name = $"cfg-role-{Guid.NewGuid():n}", SystemCapabilities = capabilities.ToList() };
        await using var session = fixture.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(role);
        await session.SaveChangesAsync(Ct);
        return role.Id;
    }

    /// <summary>A role of <paramref name="tenant"/> holding exactly these capabilities, under a name nobody else uses.</summary>
    public async Task<Guid> TenantRoleAsync(string tenant, params string[] capabilities)
    {
        var role = new Role { Id = Guid.NewGuid(), Name = $"cfg-role-{Guid.NewGuid():n}", TenantSlug = tenant, SystemCapabilities = capabilities.ToList() };
        await using var session = fixture.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(role);
        await session.SaveChangesAsync(Ct);
        return role.Id;
    }

    /// <summary>
    /// A signed-in caller holding <paramref name="globalRoles"/> on the user and, in
    /// <paramref name="tenant"/>, <paramref name="memberRoles"/> through a membership. A null tenant
    /// is the default one.
    /// </summary>
    public async Task<HttpClient> ClientAsync(string? tenant, Guid[] globalRoles, Guid[]? memberRoles = null)
    {
        var userId = Guid.NewGuid();
        await using (var session = fixture.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Store(new User
            {
                Id = userId,
                Username = $"cfg-{userId:n}",
                Email = $"cfg-{userId:n}@example.com",
                RoleIds = globalRoles.ToList(),
            });

            if (tenant is not null)
            {
                session.Store(new Membership
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    TenantSlug = tenant,
                    Status = MembershipStatus.Active,
                    RoleIds = (memberRoles ?? globalRoles).ToList(),
                });
            }

            await session.SaveChangesAsync(Ct);
        }

        var claims = new Dictionary<string, string> { ["Username"] = $"cfg-{userId:n}" };
        if (tenant is not null)
        {
            claims["tenant"] = tenant;
        }

        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", fixture.CreateToken(["cfg-caller"], userId.ToString(), claims));
        if (tenant is not null)
        {
            client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        }

        client.DefaultRequestHeaders.Add(
            TestRemoteIpFilter.Header, $"10.92.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}");
        return client;
    }

    public Task<HttpClient> SuperAdminAsync(string? tenant) => ClientAsync(tenant, [SystemRoles.SuperAdminRoleId]);

    public static async Task<(HttpResponseMessage Response, JsonElement Body)> ExportAsync(HttpClient client, string? sections = null)
    {
        var response = await client.GetAsync(sections is null ? "/api/config/export" : $"/api/config/export?sections={sections}", Ct);
        return (response, await BodyAsync(response));
    }

    public static async Task<(HttpResponseMessage Response, JsonElement Body)> PlanAsync(HttpClient client, object document)
    {
        var response = await client.PostAsJsonAsync("/api/config/plan", new { document }, Ct);
        return (response, await BodyAsync(response));
    }

    public static async Task<(HttpResponseMessage Response, JsonElement Body)> ApplyAsync(
        HttpClient client, object document, bool allowDestructive = false)
    {
        var response = await client.PostAsJsonAsync("/api/config/apply", new { document, allowDestructive }, Ct);
        return (response, await BodyAsync(response));
    }

    /// <summary>The changes a plan response names, as "section/key kind".</summary>
    public static List<string> Changes(JsonElement plan) =>
        plan.GetProperty("sections").EnumerateArray()
            .SelectMany(s => s.GetProperty("changes").EnumerateArray()
                .Select(c => $"{s.GetProperty("section").GetString()}/{c.GetProperty("key").GetString()} {c.GetProperty("kind").GetString()}"))
            .ToList();

    public async Task<T?> InTenantAsync<T>(string? tenant, Func<IQuerySession, Task<T>> read)
    {
        var store = fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = tenant is null ? store.QuerySession() : store.QuerySession(tenant);
        return await read(session);
    }

    public async Task StoreInTenantAsync(string? tenant, params object[] documents)
    {
        var store = fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = tenant is null ? store.LightweightSession() : store.LightweightSession(tenant);
        foreach (var document in documents)
        {
            session.Store(document);
        }

        await session.SaveChangesAsync(Ct);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        return string.IsNullOrWhiteSpace(text) || text[0] is not ('{' or '[')
            ? JsonDocument.Parse(JsonSerializer.Serialize(new { raw = text })).RootElement
            : JsonDocument.Parse(text).RootElement;
    }

    public static ContentTypeDefinition Type(string name, params (string Name, string Type)[] fields) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        DisplayName = name,
        Fields = fields.Select(f => new FieldDefinition { Name = f.Name, DisplayName = f.Name, Type = f.Type }).ToList(),
    };
}

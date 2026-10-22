using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Notifications;

/// <summary>Tenants, users and signed-in clients for the notification tests.</summary>
internal sealed class NotificationCallers(IntegrationTestFixture fixture)
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async Task<string> TenantAsync()
    {
        var slug = $"ntf-{Guid.NewGuid():n}"[..16];
        await using var session = fixture.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, Status = TenantStatus.Active });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    /// <summary>A stored user who is an active member of each tenant named, holding <paramref name="roles"/>.</summary>
    public async Task<Guid> UserAsync(Guid[]? roles = null, params string[] tenants)
    {
        var id = Guid.NewGuid();
        await using var session = fixture.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(new User
        {
            Id = id,
            Username = $"ntf-{id:n}",
            Email = $"ntf-{id:n}@example.com",
            RoleIds = (roles ?? []).ToList(),
        });
        foreach (var tenant in tenants)
        {
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = id,
                TenantSlug = tenant,
                Status = MembershipStatus.Active,
                RoleIds = (roles ?? []).ToList(),
            });
        }
        await session.SaveChangesAsync(Ct);
        return id;
    }

    public HttpClient ClientFor(Guid userId, string? tenant = null)
    {
        var claims = new Dictionary<string, string> { ["Username"] = $"ntf-{userId:n}" };
        if (tenant is not null) claims["tenant"] = tenant;

        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", fixture.CreateToken(["ntf-caller"], userId.ToString(), claims));
        if (tenant is not null) client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        client.DefaultRequestHeaders.Add(
            TestRemoteIpFilter.Header, $"10.93.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}");
        return client;
    }

    public async Task<HttpClient> SuperAdminAsync(string? tenant = null)
    {
        var id = await UserAsync([SystemRoles.SuperAdminRoleId], tenant is null ? [] : [tenant]);
        return ClientFor(id, tenant);
    }

    public Task<HttpResponseMessage> AnonymousGetAsync(string url) => fixture.CreateClient().GetAsync(url, Ct);

    public static async Task<Guid> RaiseAsync(
        HttpClient raiser, Guid recipient, string type = "task.due", string title = "A task is due")
    {
        var response = await raiser.PostAsJsonAsync("/api/notifications", new { recipientUserId = recipient, type, title }, Ct);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return body.RootElement.GetProperty("id").GetGuid();
    }

    public static async Task<JsonElement> ListAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync($"/api/notifications{query}", Ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    public static List<Guid> Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
}

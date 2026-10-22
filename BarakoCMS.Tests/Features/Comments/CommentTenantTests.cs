using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Comments;

/// <summary>A tenant's comments are invisible to every other tenant, by id or by listing.</summary>
[Collection("Sequential")]
public class CommentTenantTests
{
    private readonly IntegrationTestFixture _factory;

    public CommentTenantTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> TenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"club-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    private async Task<HttpClient> MemberAsync(string tenantSlug, string ip)
    {
        var userId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = $"ct-{Guid.NewGuid():n}"[..14],
                Email = $"ct-{Guid.NewGuid():n}@example.com",
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TenantSlug = tenantSlug,
                Status = MembershipStatus.Active,
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            await session.SaveChangesAsync(Ct);
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(
                roles: ["SuperAdmin"],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = tenantSlug }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenantSlug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ip);
        return client;
    }

    private async Task<Guid> EntryInAsync(string tenantSlug)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenantSlug);

        var id = Guid.NewGuid();
        session.Store(new Content
        {
            Id = id,
            ContentType = $"ct_{Guid.NewGuid():n}"[..12],
            Status = ContentStatus.Published,
            Data = new Dictionary<string, object> { ["Title"] = "a record" },
        });
        await session.SaveChangesAsync(Ct);
        return id;
    }

    private static async Task<int> CountAsync(HttpClient client, Guid entry)
    {
        var res = await client.GetAsync($"/api/comments?targetType=content&targetId={entry}", Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        return (await res.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("totalItems").GetInt32();
    }

    [Fact]
    public async Task A_member_reads_and_posts_in_their_own_tenant()
    {
        var acme = await TenantAsync();
        var entry = await EntryInAsync(acme);
        var client = await MemberAsync(acme, "203.0.113.141");

        var res = await client.PostAsJsonAsync(
            "/api/comments", new { targetType = "content", targetId = entry, body = "inside" }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync(Ct));
        (await CountAsync(client, entry)).Should().Be(1);
    }

    [Fact]
    public async Task Another_tenants_record_has_no_thread_to_read_or_to_add_to()
    {
        var acme = await TenantAsync();
        var globex = await TenantAsync();
        var globexEntry = await EntryInAsync(globex);
        var globexClient = await MemberAsync(globex, "203.0.113.142");
        var acmeClient = await MemberAsync(acme, "203.0.113.143");
        (await globexClient.PostAsJsonAsync(
            "/api/comments", new { targetType = "content", targetId = globexEntry, body = "globex only" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var read = await acmeClient.GetAsync($"/api/comments?targetType=content&targetId={globexEntry}", Ct);
        var post = await acmeClient.PostAsJsonAsync(
            "/api/comments", new { targetType = "content", targetId = globexEntry, body = "from acme" }, Ct);

        read.StatusCode.Should().Be(HttpStatusCode.NotFound);
        post.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CountAsync(globexClient, globexEntry)).Should().Be(1, "acme's refused post left nothing in globex");
    }

    [Fact]
    public async Task A_comment_stored_in_one_tenant_is_not_in_another_tenants_partition()
    {
        var acme = await TenantAsync();
        var globex = await TenantAsync();
        var entry = await EntryInAsync(acme);
        var acmeClient = await MemberAsync(acme, "203.0.113.144");
        var posted = await acmeClient.PostAsJsonAsync(
            "/api/comments", new { targetType = "content", targetId = entry, body = "acme only" }, Ct);
        posted.StatusCode.Should().Be(HttpStatusCode.Created);

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await using var acmeSession = store.QuerySession(acme);
        await using var globexSession = store.QuerySession(globex);

        (await acmeSession.Query<BarakoCMS.Comments.Comment>().CountAsync(Ct)).Should().Be(1);
        (await globexSession.Query<BarakoCMS.Comments.Comment>().CountAsync(Ct)).Should().Be(0);
    }
}

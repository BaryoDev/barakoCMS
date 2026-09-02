using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Issue #464: a per-item permission decision must not outlive the item state it was based on.
/// </summary>
/// <remarks>
/// The decision cache was keyed on tenant, user, type, action and the item's id, with a five minute
/// expiry, and nothing on the content write path invalidated it. A decision that depends on the
/// item's contents therefore kept applying after those contents changed. It failed open, which is
/// the direction that matters: a stale denial is an inconvenience, a stale grant is an
/// authorisation check that has stopped checking.
///
/// Both tests here go through HTTP twice with a write in between, because one call can never show
/// this. The first call is what populates the cache and the second is the assertion.
/// </remarks>
[Collection("Sequential")]
public class PermissionCacheItemStateTests
{
    private readonly IntegrationTestFixture _factory;

    public PermissionCacheItemStateTests(IntegrationTestFixture factory) => _factory = factory;

    private const string Type = "cachedpost";

    /// <summary>A role that may update an entry only while it is still a draft.</summary>
    private static PermissionRule WhileDraftOnly() => new()
    {
        Enabled = true,
        Conditions = new Dictionary<string, object>
        {
            ["$status"] = new Dictionary<string, object> { ["_eq"] = "Draft" },
        },
    };

    [Fact]
    public async Task A_grant_conditional_on_status_stops_applying_the_moment_the_status_changes()
    {
        var (client, _) = await ScopedUserAsync();
        var id = await DraftAsync(client);

        // 1. Allowed while it is a draft. This is the call that used to cache "allow" under the id.
        var first = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            data = new Dictionary<string, object> { ["Title"] = "edited while draft" },
        });
        first.StatusCode.Should().Be(HttpStatusCode.OK,
            "the rule grants update while the entry is a draft, and without this the second call proves nothing");

        // 2. Published behind the endpoint's back, which is the honest reproduction: the state the
        //    decision was based on changed, and no write path tells the cache about it.
        await SetStatusAsync(id, ContentStatus.Published);

        // 3. The rule now says no. A cached decision would still say yes for five minutes.
        var second = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            data = new Dictionary<string, object> { ["Title"] = "edited after publishing" },
        });
        second.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the entry is no longer a draft, so the condition no longer holds");

        // The grant is what is being tested, so check the write did not land either. A 403 with the
        // edit applied would be the same defect wearing a different status code.
        (await TitleAsync(id)).Should().Be("edited while draft");
    }

    /// <summary>
    /// The other direction, and the reason this is a fix rather than a removal: a decision that
    /// went the wrong way must also stop applying. This one fails open in reverse, so it is the
    /// cheaper half, but a fix that only unsticks denials would leave the grant stuck.
    /// </summary>
    [Fact]
    public async Task A_denial_conditional_on_status_stops_applying_the_moment_the_status_changes()
    {
        var (client, _) = await ScopedUserAsync();
        var id = await DraftAsync(client);
        await SetStatusAsync(id, ContentStatus.Published);

        var first = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            data = new Dictionary<string, object> { ["Title"] = "refused" },
        });
        first.StatusCode.Should().Be(HttpStatusCode.Forbidden, "it is published, so the condition does not hold");

        await SetStatusAsync(id, ContentStatus.Draft);

        var second = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            data = new Dictionary<string, object> { ["Title"] = "allowed again" },
        });
        second.StatusCode.Should().Be(HttpStatusCode.OK, "it is a draft again, so the condition holds again");
    }

    /// <summary>
    /// The type-level decision is still cached, and that is the half worth keeping: there is no item
    /// in the question, so there is no item state to go stale. Asserted through the behaviour that
    /// caching is for, a revocation that is invisible until something invalidates: the role is
    /// emptied in the database, where no endpoint runs and nothing cancels an expiration token, and
    /// the answer must still be the cached one.
    /// </summary>
    [Fact]
    public async Task The_type_level_decision_is_still_cached()
    {
        var (client, _, roleId) = await ScopedUserWithRoleAsync();

        var first = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = Type,
            data = new Dictionary<string, object> { ["Title"] = "first" },
        });
        first.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", first.StatusCode,
            await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var role = await session.LoadAsync<Role>(roleId, TestContext.Current.CancellationToken);
            role!.Permissions = [];
            session.Store(role);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var second = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = Type,
            data = new Dictionary<string, object> { ["Title"] = "second" },
        });
        second.IsSuccessStatusCode.Should().BeTrue(
            "the create decision has no item in it, so it is still served from the cache and a silent database edit does not reach it");
    }

    /// <summary>
    /// Created through the API rather than stored straight into the document, because an update is
    /// an append and appending to a stream that was never opened is a 500 rather than a permission
    /// answer.
    /// </summary>
    private async Task<Guid> DraftAsync(HttpClient client)
    {
        var created = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = Type,
            data = new Dictionary<string, object> { ["Title"] = "draft" },
        });
        created.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", created.StatusCode,
            await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var body = await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return body.GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Moved by an admin through the real endpoint, which is how a status changes in production and
    /// is also the point: no write path invalidates the permission cache, so the decision cached
    /// under this entry's id survives the change that should have ended it.
    /// </summary>
    private async Task SetStatusAsync(Guid id, ContentStatus status)
    {
        var adminId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            // A stored user holding the seeded SuperAdmin role, not just a token saying so. The
            // status endpoint asks the permission resolver, and the resolver's bypass reads role ids
            // off the user document.
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = adminId,
                Username = $"cache-admin-{adminId:n}",
                Email = $"cache-admin-{adminId:n}@example.com",
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: ["SuperAdmin"], userId: adminId.ToString()));

        var moved = await admin.PutAsJsonAsync($"/api/contents/{id}/status", new { newStatus = status });
        moved.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", moved.StatusCode,
            await moved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private async Task<string?> TitleAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var content = await session.LoadAsync<Content>(id, TestContext.Current.CancellationToken);
        return content!.Data.TryGetValue("Title", out var title) ? title?.ToString() : null;
    }

    private async Task<(HttpClient Client, Guid UserId)> ScopedUserAsync()
    {
        var (client, userId, _) = await ScopedUserWithRoleAsync();
        return (client, userId);
    }

    private async Task<(HttpClient Client, Guid UserId, Guid RoleId)> ScopedUserWithRoleAsync()
    {
        var roleId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Role
            {
                Id = roleId,
                Name = $"Drafts_{roleId:n}",
                Permissions =
                [
                    new ContentTypePermission
                    {
                        ContentTypeSlug = Type,
                        Read = new PermissionRule { Enabled = true },
                        Create = new PermissionRule { Enabled = true },
                        Update = WhileDraftOnly(),
                    },
                ],
            });
            session.Store(new User
            {
                Id = userId,
                Username = $"drafts-{userId:n}",
                Email = $"drafts-{userId:n}@example.com",
                RoleIds = [roleId],
            });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: ["Editor"], userId: userId.ToString()));
        return (client, userId, roleId);
    }
}

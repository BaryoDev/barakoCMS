using System.Net;
using System.Net.Http.Json;
using Xunit;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

[Collection("Sequential")]
public class PermissionCacheInvalidationTests
{
    private readonly HttpClient _client;
    private readonly IntegrationTestFixture _factory;

    public PermissionCacheInvalidationTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task RevokingRolePermission_TakesEffectImmediately_NotAfterCacheTtl()
    {
        var roleId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var contentId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

            // A role that can READ content type "doc".
            session.Store(new Role
            {
                Id = roleId,
                Name = "DocReader",
                Permissions = new List<ContentTypePermission>
                {
                    new ContentTypePermission
                    {
                        ContentTypeSlug = "doc",
                        Read = new PermissionRule { Enabled = true }
                    }
                }
            });

            session.Store(new User
            {
                Id = userId,
                Username = $"reader-{userId}",
                Email = $"{userId}@test.com",
                RoleIds = new List<Guid> { roleId }
            });

            session.Store(new Content
            {
                Id = contentId,
                ContentType = "doc",
                Data = new Dictionary<string, object> { { "Title", "Doc" } },
                Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Public
            });

            await session.SaveChangesAsync();
        }

        var userToken = _factory.CreateToken(roles: new[] { "Editor" }, userId: userId.ToString());
        var adminToken = _factory.CreateToken(roles: new[] { "SuperAdmin" });

        // 1. User can read the content (result gets cached).
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", userToken);
        var before = await _client.GetAsync($"/api/contents/{contentId}");
        before.StatusCode.Should().Be(HttpStatusCode.OK);

        // 2. Admin revokes the read permission from the role (empties its permissions).
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
        var update = await _client.PutAsJsonAsync($"/api/roles/{roleId}", new
        {
            Id = roleId,
            Name = "DocReader",
            Description = "",
            Permissions = new List<ContentTypePermission>(),
            SystemCapabilities = new List<string>()
        });
        update.EnsureSuccessStatusCode();

        // 3. The revocation must apply immediately (cache invalidated), not after the 5-minute TTL.
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", userToken);
        var after = await _client.GetAsync($"/api/contents/{contentId}");
        after.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
    /// <summary>
    /// A revocation still applies when the invalidation bookkeeping has been lost.
    /// </summary>
    /// <remarks>
    /// The test above passes on the broken implementation too, because it runs inside the window
    /// where the version counter is still alive. That window is what made this survive.
    ///
    /// Invalidation used to bump a counter that formed part of the cache key, and that counter was
    /// itself an entry in the same cache: same five minute expiry, same size limit, same eviction
    /// under pressure. When it disappeared, the next invalidation read 0, wrote 1, and rebuilt the
    /// key that was already cached. The revoked permission came back, and the log line said
    /// "Invalidated permission cache" either way.
    ///
    /// Removing the counter here is how that state is reached without waiting five minutes or
    /// filling the cache. On the current implementation there is no such entry and the removal does
    /// nothing, which is the point: invalidation no longer depends on anything that can expire out
    /// from under it.
    /// </remarks>
    [Fact]
    public async Task A_revocation_applies_even_after_the_invalidation_counter_is_gone()
    {
        var roleId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var contentId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

            session.Store(new Role
            {
                Id = roleId,
                Name = $"DocReader-{roleId:n}"[..20],
                Permissions =
                [
                    new ContentTypePermission
                    {
                        ContentTypeSlug = "doc",
                        Read = new PermissionRule { Enabled = true },
                    },
                ],
            });
            session.Store(new User
            {
                Id = userId,
                Username = $"reader-{userId}",
                Email = $"{userId}@example.com",
                RoleIds = [roleId],
            });
            session.Store(new Content
            {
                Id = contentId,
                ContentType = "doc",
                Data = new Dictionary<string, object> { { "Title", "Doc" } },
                Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Public,
            });
            await session.SaveChangesAsync();
        }

        var userToken = _factory.CreateToken(roles: ["Reader"], userId: userId.ToString());
        var adminToken = _factory.CreateToken(roles: ["SuperAdmin"]);

        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", userToken);
        (await _client.GetAsync($"/api/contents/{contentId}")).StatusCode
            .Should().Be(HttpStatusCode.OK, "the decision has to be cached before it can go stale");

        // The counter expires or is evicted. Removing it is the same state, reached in a millisecond.
        using (var scope = _factory.Services.CreateScope())
        {
            var cache = scope.ServiceProvider
                .GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
            cache.Remove($"perm_version:{userId}");
            cache.Remove("perm_version:global");
        }

        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
        var update = await _client.PutAsJsonAsync($"/api/roles/{roleId}", new
        {
            Id = roleId,
            Name = $"DocReader-{roleId:n}"[..20],
            Description = "",
            Permissions = new List<ContentTypePermission>(),
            SystemCapabilities = new List<string>(),
        });
        update.EnsureSuccessStatusCode();

        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", userToken);
        var after = await _client.GetAsync($"/api/contents/{contentId}");

        after.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the permission was revoked. If the cached decision survived, invalidation depended on "
          + "bookkeeping stored in the very cache it was invalidating");
    }

}

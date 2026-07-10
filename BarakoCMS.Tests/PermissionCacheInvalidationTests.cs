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
}

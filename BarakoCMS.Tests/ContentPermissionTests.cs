using System.Net;
using System.Net.Http.Json;
using BarakoCMS.Tests;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

[Collection("Sequential")]
public class ContentPermissionTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public ContentPermissionTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<(string Token, Guid UserId)> SetupUserWithPermission(string contentType, string action, bool enabled)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        using var session = store.LightweightSession();

        // 1. Create Role
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Role_{Guid.NewGuid()}",
            Permissions = new List<ContentTypePermission>
            {
                new()
                {
                    ContentTypeSlug = contentType,
                    Create = action == "create" ? new PermissionRule { Enabled = enabled } : new PermissionRule { Enabled = false },
                    Read = action == "read" ? new PermissionRule { Enabled = enabled } : new PermissionRule { Enabled = false },
                    Update = action == "update" ? new PermissionRule { Enabled = enabled } : new PermissionRule { Enabled = false },
                    Delete = action == "delete" ? new PermissionRule { Enabled = enabled } : new PermissionRule { Enabled = false }
                }
            }
        };
        session.Store(role);

        // 2. Create User
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"user_{Guid.NewGuid()}",
            Email = $"test_{Guid.NewGuid()}@example.com",
            RoleIds = new List<Guid> { role.Id }
        };
        session.Store(user);
        await session.SaveChangesAsync();

        // 3. Mint Token using the fixture's helper to avoid FastEndpoints static state issues
        var token = _factory.CreateToken(
            roles: new[] { role.Name },
            userId: user.Id.ToString());

        return (token, user.Id);
    }

    [Fact]
    public async Task CreateContent_WithPermission_ShouldSucceed()
    {
        // Arrange
        var (token, _) = await SetupUserWithPermission("article", "create", true);
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Act
        var res = await _client.PostAsJsonAsync("/api/contents", new barakoCMS.Features.Content.Create.Request
        {
            ContentType = "article",
            Data = new Dictionary<string, object> { { "title", "Test Title" } }
        });

        // Debug: Output response body if not 200
        if (res.StatusCode != HttpStatusCode.OK)
        {
            var body = await res.Content.ReadAsStringAsync();
            Console.WriteLine($"[TEST DEBUG] Response Status: {res.StatusCode}, Body: {body}");
        }

        // Assert
        res.StatusCode.Should().Be(HttpStatusCode.OK, because: "user has permission to create content");
    }

    [Fact]
    public async Task CreateContent_WithoutPermission_ShouldReturnForbidden()
    {
        // Arrange
        var (token, _) = await SetupUserWithPermission("article", "create", false);
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Act
        var res = await _client.PostAsJsonAsync("/api/contents", new barakoCMS.Features.Content.Create.Request
        {
            ContentType = "article",
            Data = new Dictionary<string, object> { { "title", "Test Title" } }
        });

        // Assert
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetContent_WithPermission_ShouldSucceed()
    {
        // Arrange
        var (token, userId) = await SetupUserWithPermission("article", "read", true);
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Create content directly in DB so we can try to read it
        var contentId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            using var session = store.LightweightSession();
            var content = new Content
            {
                Id = contentId,
                ContentType = "article",
                Data = new Dictionary<string, object> { { "title", "Readable Content" } },
                CreatedAt = DateTime.UtcNow
            };
            session.Store(content);
            await session.SaveChangesAsync();
        }

        // Act
        var res = await _client.GetAsync($"/api/contents/{contentId}");

        // Assert
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetContent_WithoutPermission_ShouldReturnForbidden()
    {
        // Arrange
        var (token, _) = await SetupUserWithPermission("article", "read", false);
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var contentId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            using var session = store.LightweightSession();
            var content = new Content
            {
                Id = contentId,
                ContentType = "article",
                Data = new Dictionary<string, object> { { "title", "Secret Content" } },
                CreatedAt = DateTime.UtcNow
            };
            session.Store(content);
            await session.SaveChangesAsync();
        }

        // Act
        var res = await _client.GetAsync($"/api/contents/{contentId}");

        // Assert
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

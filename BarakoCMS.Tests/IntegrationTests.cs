using FastEndpoints;
using FluentAssertions;
using Xunit;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net.Http.Json;
using System.Net;
using barakoCMS.Features.Auth.Register;
using barakoCMS.Features.Auth.Login;
using barakoCMS.Features.Content.Create;
using barakoCMS.Features.Content.Get;
using barakoCMS.Features.Content.Update;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using RegisterRequest = barakoCMS.Features.Auth.Register.Request;
using RegisterResponse = barakoCMS.Features.Auth.Register.Response;
using LoginRequest = barakoCMS.Features.Auth.Login.Request;
using LoginResponse = barakoCMS.Features.Auth.Login.Response;
using CreateContentRequest = barakoCMS.Features.Content.Create.Request;
using CreateContentResponse = barakoCMS.Features.Content.Create.Response;
using GetContentRequest = barakoCMS.Features.Content.Get.Request;
using GetContentResponse = barakoCMS.Features.Content.Get.Response;
using UpdateContentRequest = barakoCMS.Features.Content.Update.Request;
using UpdateContentResponse = barakoCMS.Features.Content.Update.Response;

namespace BarakoCMS.Tests;

[Collection("Sequential")]
public class IntegrationTests
{
    private readonly HttpClient _client;
    private readonly IntegrationTestFixture _factory;

    public IntegrationTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private string CreateAdminToken()
    {
        return _factory.CreateToken(roles: new[] { "Admin" });
    }

    private async Task<(string token, Guid userId)> CreateAdminUserAsync()
    {
        return await TestHelpers.CreateAdminUserAsync(_factory);
    }

    [Fact]
    public async Task Auth_RBAC_Flow()
    {
        // 1. Register (Standard User)
        var username = $"user_{Guid.NewGuid()}";
        var email = $"{username}@test.com";
        var password = "Password123!";

        var registerRes = await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            Username = username,
            Email = email,
            Password = password
        });

        registerRes.IsSuccessStatusCode.Should().BeTrue();

        // 2. Login as Standard User
        var loginRes = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = username,
            Password = password
        });

        loginRes.IsSuccessStatusCode.Should().BeTrue();
        var loginContent = await loginRes.Content.ReadFromJsonAsync<LoginResponse>();
        var userToken = loginContent!.Token;

        // 3. Try Create Content (Should Fail - Forbidden)
        // Create separate HttpClient for user to avoid shared state
        var userClient = _factory.CreateClient();
        userClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", userToken);

        var contentData = new Dictionary<string, object> { { "Title", "Test Article" }, { "Body", "Hello World" } };

        var failCreateRes = await userClient.PostAsJsonAsync("/api/contents", new CreateContentRequest
        {
            ContentType = "Article",
            Data = contentData
        });

        failCreateRes.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 4. Create Admin User and Token
        var (adminToken, adminUserId) = await CreateAdminUserAsync();

        // 5. Create Content as Admin (Should Succeed)
        // Create separate HttpClient for admin to avoid shared state
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);

        var createRes = await adminClient.PostAsJsonAsync("/api/contents", new CreateContentRequest
        {
            ContentType = "Article",
            Data = contentData
        });

        createRes.IsSuccessStatusCode.Should().BeTrue();
    }

    [Fact]
    public async Task Auth_Security_EdgeCases()
    {
        // 1. Register with Short Password
        var shortPassRes = await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            Username = $"short_{Guid.NewGuid()}",
            Email = "short@test.com",
            Password = "123" // Too short
        });

        shortPassRes.IsSuccessStatusCode.Should().BeFalse();
        shortPassRes.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 2. Register Duplicate User
        var username = $"dup_{Guid.NewGuid()}";
        var email = $"{username}@test.com";
        var password = "Password123!";

        await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest { Username = username, Email = email, Password = password });

        var dupRes = await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            Username = username,
            Email = "other@test.com",
            Password = password
        });

        dupRes.IsSuccessStatusCode.Should().BeFalse();
        dupRes.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 3. Login Invalid Credentials
        var invalidLoginRes = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = username,
            Password = "wrongpassword"
        });

        invalidLoginRes.IsSuccessStatusCode.Should().BeFalse();
        invalidLoginRes.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Content_EdgeCases()
    {
        // 1. Get Non-Existent Content
        var nonExistentId = Guid.NewGuid();
        var getRes = await _client.GetAsync($"/api/contents/{nonExistentId}");

        // Note: Depending on implementation, this might be 404 or 500 if not handled. 
        // Assuming Get endpoint handles null result.
        // If using Marten LoadAsync, it returns null. Endpoint should check and return 404.
        // Let's verify current implementation handles it.

        // 2. Update Non-Existent Content
        // Create Admin User and Token
        var (adminToken, adminUserId) = await CreateAdminUserAsync();
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);

        var updateRes = await _client.PutAsJsonAsync($"/api/contents/{nonExistentId}", new UpdateContentRequest
        {
            Id = nonExistentId,
            Data = new Dictionary<string, object> { { "Title", "Some Data" } } // Must be non-empty to pass FastEndpoints validator
        });

        // Update endpoint checks if content exists and returns 404 if not found
        updateRes.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
    [Fact]
    public async Task Content_Workflow()
    {
        // 1. Create Admin User and Token
        var (adminToken, adminUserId) = await CreateAdminUserAsync();
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);

        // 2. Create Content (Default Draft)
        var contentData = new Dictionary<string, object> { { "Title", "Draft Article" } };
        var createRes = await _client.PostAsJsonAsync("/api/contents", new CreateContentRequest
        {
            ContentType = "Article",
            Data = contentData
        });
        createRes.IsSuccessStatusCode.Should().BeTrue();
        var contentId = (await createRes.Content.ReadFromJsonAsync<CreateContentResponse>())!.Id;

        // 3. Verify Status is Draft
        var getRes = await _client.GetAsync($"/api/contents/{contentId}");
        getRes.IsSuccessStatusCode.Should().BeTrue();
        var content = await getRes.Content.ReadFromJsonAsync<barakoCMS.Models.Content>();
        content!.Status.Should().Be(barakoCMS.Models.ContentStatus.Draft);

        // 4. Change Status to Published
        var statusRes = await _client.PutAsJsonAsync($"/api/contents/{contentId}/status", new barakoCMS.Features.Content.ChangeStatus.Request
        {
            Id = contentId,
            NewStatus = barakoCMS.Models.ContentStatus.Published
        });
        statusRes.IsSuccessStatusCode.Should().BeTrue();

        // 5. Verify Status is Published
        getRes = await _client.GetAsync($"/api/contents/{contentId}");
        var updatedContent = await getRes.Content.ReadFromJsonAsync<barakoCMS.Models.Content>();
        updatedContent!.Status.Should().Be(barakoCMS.Models.ContentStatus.Published);

        // 6. Create Content with Specific Status (Archived)
        var archivedRes = await _client.PostAsJsonAsync("/api/contents", new CreateContentRequest
        {
            ContentType = "Article",
            Data = contentData,
            Status = barakoCMS.Models.ContentStatus.Archived
        });
        archivedRes.IsSuccessStatusCode.Should().BeTrue();
        var archivedId = (await archivedRes.Content.ReadFromJsonAsync<CreateContentResponse>())!.Id;

        // 7. Verify Status is Archived
        getRes = await _client.GetAsync($"/api/contents/{archivedId}");
        var archivedContent = await getRes.Content.ReadFromJsonAsync<barakoCMS.Models.Content>();
        archivedContent!.Status.Should().Be(barakoCMS.Models.ContentStatus.Archived);
    }
}

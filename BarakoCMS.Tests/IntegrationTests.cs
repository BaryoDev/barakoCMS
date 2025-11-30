using FastEndpoints;
using FluentAssertions;
using Xunit;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net.Http.Json;
using barakoCMS.Features.Auth.Register;
using barakoCMS.Features.Auth.Login;
using barakoCMS.Features.Content.Create;
using barakoCMS.Features.Content.Get;
using RegisterRequest = barakoCMS.Features.Auth.Register.Request;
using RegisterResponse = barakoCMS.Features.Auth.Register.Response;
using LoginRequest = barakoCMS.Features.Auth.Login.Request;
using LoginResponse = barakoCMS.Features.Auth.Login.Response;
using CreateContentRequest = barakoCMS.Features.Content.Create.Request;
using CreateContentResponse = barakoCMS.Features.Content.Create.Response;
using GetContentRequest = barakoCMS.Features.Content.Get.Request;
using GetContentResponse = barakoCMS.Features.Content.Get.Response;

namespace BarakoCMS.Tests;

public class IntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public IntegrationTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Auth_And_Content_Flow()
    {
        // 1. Register
        var username = $"user_{Guid.NewGuid()}";
        var email = $"{username}@test.com";
        var password = "password123";

        var registerRes = await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            Username = username,
            Email = email,
            Password = password
        });

        registerRes.IsSuccessStatusCode.Should().BeTrue();
        var registerContent = await registerRes.Content.ReadFromJsonAsync<RegisterResponse>();
        registerContent!.Message.Should().Be("User registered successfully");

        // 2. Login
        var loginRes = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = username,
            Password = password
        });

        loginRes.IsSuccessStatusCode.Should().BeTrue();
        var loginContent = await loginRes.Content.ReadFromJsonAsync<LoginResponse>();
        loginContent!.Token.Should().NotBeNullOrEmpty();

        var token = loginContent.Token;
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // 3. Create Content
        var contentData = new Dictionary<string, object> { { "Title", "Test Article" }, { "Body", "Hello World" } };
        var createRes = await _client.PostAsJsonAsync("/api/contents", new CreateContentRequest
        {
            ContentType = "Article",
            Data = contentData
        });

        createRes.IsSuccessStatusCode.Should().BeTrue();
        var createContent = await createRes.Content.ReadFromJsonAsync<CreateContentResponse>();
        createContent!.Id.Should().NotBeEmpty();

        // 4. Get Content
        var getRes = await _client.GetAsync($"/api/contents/{createContent.Id}");

        getRes.IsSuccessStatusCode.Should().BeTrue();
        var getContent = await getRes.Content.ReadFromJsonAsync<GetContentResponse>();
        getContent!.Data["Title"].ToString().Should().Be("Test Article");
    }
}

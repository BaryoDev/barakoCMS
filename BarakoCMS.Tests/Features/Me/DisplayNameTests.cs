using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.Me;

/// <summary>
/// A user's display name: <c>GET /api/me</c> returns it, falls back to the username when unset, and
/// <c>PUT /api/me/profile</c> sets and clears it (#1160).
/// </summary>
[Collection("Integration1")]
public class DisplayNameTests
{
    private const string MeRoute = "/api/me";
    private const string ProfileRoute = "/api/me/profile";

    private readonly IntegrationTestFixture _factory;

    public DisplayNameTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Guid Id, string Username)> UserAsync(string? displayName = null)
    {
        var id = Guid.NewGuid();
        var username = $"dn-{id:n}";
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new User
        {
            Id = id,
            Username = username,
            Email = $"{username}@example.com",
            DisplayName = displayName,
        });
        await session.SaveChangesAsync(Ct);
        return (id, username);
    }

    private HttpClient Client(Guid userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: ["User"], userId: userId.ToString()));
        return client;
    }

    private static async Task<string?> MeDisplayNameAsync(HttpClient client)
    {
        var response = await client.GetAsync(MeRoute, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var parsed = JsonDocument.Parse(body);
        parsed.RootElement.TryGetProperty("displayName", out var name).Should().BeTrue("the response carries displayName");
        return name.GetString();
    }

    private async Task<string?> StoredDisplayNameAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await query.LoadAsync<User>(id, Ct))!.DisplayName;
    }

    [Fact]
    public async Task Me_returns_the_stored_display_name()
    {
        var (id, username) = await UserAsync("Ada Lovelace");

        var name = await MeDisplayNameAsync(Client(id));

        name.Should().Be("Ada Lovelace").And.NotBe(username);
    }

    [Fact]
    public async Task Me_falls_back_to_the_username_when_no_display_name_is_set()
    {
        var (id, username) = await UserAsync();

        (await MeDisplayNameAsync(Client(id))).Should().Be(username);
    }

    [Fact]
    public async Task Me_falls_back_to_the_username_when_the_stored_display_name_is_blank()
    {
        var (id, username) = await UserAsync("   ");

        (await MeDisplayNameAsync(Client(id))).Should().Be(username);
    }

    [Fact]
    public async Task A_user_sets_their_display_name_and_me_returns_it()
    {
        var (id, username) = await UserAsync();
        var client = Client(id);

        var response = await client.PutAsJsonAsync(ProfileRoute, new { DisplayName = "  Grace Hopper  " }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await StoredDisplayNameAsync(id)).Should().Be("Grace Hopper", "it is stored trimmed");
        (await MeDisplayNameAsync(client)).Should().Be("Grace Hopper").And.NotBe(username);
    }

    [Fact]
    public async Task A_blank_display_name_clears_it_and_me_falls_back_to_the_username()
    {
        var (id, username) = await UserAsync("Ada Lovelace");
        var client = Client(id);

        var response = await client.PutAsJsonAsync(ProfileRoute, new { DisplayName = "  " }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await StoredDisplayNameAsync(id)).Should().BeNull();
        (await MeDisplayNameAsync(client)).Should().Be(username);
    }

    [Fact]
    public async Task A_display_name_of_the_maximum_length_is_accepted()
    {
        var (id, _) = await UserAsync();
        var name = new string('a', 100);

        var response = await Client(id).PutAsJsonAsync(ProfileRoute, new { DisplayName = name }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await StoredDisplayNameAsync(id)).Should().Be(name);
    }

    [Fact]
    public async Task A_display_name_over_the_maximum_length_is_refused_and_not_stored()
    {
        var (id, _) = await UserAsync("Ada Lovelace");

        var response = await Client(id).PutAsJsonAsync(ProfileRoute, new { DisplayName = new string('a', 101) }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StoredDisplayNameAsync(id)).Should().Be("Ada Lovelace");
    }

    [Fact]
    public async Task A_display_name_with_a_control_character_is_refused()
    {
        var (id, _) = await UserAsync();

        var response = await Client(id).PutAsJsonAsync(ProfileRoute, new { DisplayName = "Ada\nLovelace" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StoredDisplayNameAsync(id)).Should().BeNull();
    }

    [Theory]
    [InlineData("Ada\u202ELovelace")]
    [InlineData("Ada\u200BLovelace")]
    [InlineData("\u3164")]
    [InlineData("Ada\u2028Lovelace")]
    [InlineData("Ada\U000E0041Lovelace")]
    public async Task A_display_name_with_an_invisible_formatting_character_is_refused(string name)
    {
        var (id, _) = await UserAsync();

        var response = await Client(id).PutAsJsonAsync(ProfileRoute, new { DisplayName = name }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StoredDisplayNameAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task Setting_a_display_name_changes_only_the_callers_own_user()
    {
        var (callerId, _) = await UserAsync();
        var (otherId, _) = await UserAsync("Other Person");

        var response = await Client(callerId).PutAsJsonAsync(ProfileRoute, new { DisplayName = "Mine" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StoredDisplayNameAsync(callerId)).Should().Be("Mine");
        (await StoredDisplayNameAsync(otherId)).Should().Be("Other Person");
    }

    [Fact]
    public async Task An_anonymous_caller_cannot_set_a_display_name()
    {
        var response = await _factory.CreateClient().PutAsJsonAsync(ProfileRoute, new { DisplayName = "x" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

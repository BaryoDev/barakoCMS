using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// A blueprint file may declare a token field. One that states no sensitivity applies as Hidden,
/// and one with a length outside the range is listed with the reason and not applied.
/// </summary>
/// <remarks>
/// One directory and one derived host for the class, and a tenant per test, as
/// <see cref="BlueprintFieldPresentationTests"/> does.
/// </remarks>
[Collection("Sequential")]
public class BlueprintTokenFieldTests : IDisposable
{
    private const string Blueprints = "/api/content-types/blueprints";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly string BlueprintDirectory =
        Path.Combine(Path.GetTempPath(), $"barako-blueprints-{Guid.NewGuid():N}");

    private static readonly Lock Gate = new();
    private static WebApplicationFactory<Program>? _host;

    private readonly IntegrationTestFixture _factory;

    public BlueprintTokenFieldTests(IntegrationTestFixture factory)
    {
        _factory = factory;

        Directory.CreateDirectory(BlueprintDirectory);
        File.WriteAllText(Path.Combine(BlueprintDirectory, "ticketed.json"), Ticketed);
        File.WriteAllText(Path.Combine(BlueprintDirectory, "shorttoken.json"), ShortToken);
    }

    public void Dispose()
    {
        if (Directory.Exists(BlueprintDirectory))
            Directory.Delete(BlueprintDirectory, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Ticketed = """
        {
          "name": "ticketed",
          "description": "A registration with a claim token.",
          "contentTypes": [
            {
              "name": "registration",
              "displayName": "Registration",
              "fields": [
                { "name": "Name", "displayName": "Name", "type": "string" },
                { "name": "ClaimToken", "displayName": "Claim token", "type": "token", "tokenLength": 20 }
              ]
            }
          ]
        }
        """;

    private const string ShortToken = """
        {
          "name": "shorttoken",
          "contentTypes": [
            {
              "name": "short-ticket",
              "displayName": "Ticket",
              "fields": [ { "name": "ClaimToken", "displayName": "Claim token", "type": "token", "tokenLength": 8 } ]
            }
          ]
        }
        """;

    // Never disposed: a derived host shares the fixture's server.
    private WebApplicationFactory<Program> BlueprintHost()
    {
        lock (Gate)
        {
            return _host ??= _factory.WithSetting("Blueprints:Path", BlueprintDirectory);
        }
    }

    private async Task<string> TenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"bt-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    private async Task<HttpClient> AdminInAsync(string tenantSlug)
    {
        var userId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = $"bt-{Guid.NewGuid():n}"[..14],
                Email = $"bt-{Guid.NewGuid():n}@example.com",
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

        var client = BlueprintHost().CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(
                roles: ["SuperAdmin"],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = tenantSlug }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenantSlug);
        client.DefaultRequestHeaders.Add(
            TestRemoteIpFilter.Header, $"10.9.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}");
        return client;
    }

    private sealed record ListResponse(List<ListItem> Items, List<string> Problems);

    private sealed record ListItem(
        string Name, string Description, bool BuiltIn, string? Source, List<string> ContentTypes, List<string> Errors);

    private static async Task<ListResponse> ListAsync(HttpClient client)
    {
        var response = await client.GetAsync(Blueprints, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<ListResponse>(Json, Ct))!;
    }

    private async Task<ContentTypeDefinition?> StoredAsync(string tenantSlug, string type)
    {
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(tenantSlug);
        return await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type, Ct);
    }

    [Fact]
    public async Task A_blueprint_token_field_that_states_no_sensitivity_applies_as_hidden_with_its_length()
    {
        var tenant = await TenantAsync();
        var client = await AdminInAsync(tenant);

        (await ListAsync(client)).Items.Single(i => i.Name == "ticketed").Errors.Should().BeEmpty();

        var applied = await client.PostAsync($"{Blueprints}/ticketed", null, Ct);
        applied.StatusCode.Should().Be(HttpStatusCode.OK, await applied.Content.ReadAsStringAsync(Ct));

        var stored = await StoredAsync(tenant, "registration");
        stored.Should().NotBeNull();
        var token = stored!.Fields.Single(f => f.Name == "ClaimToken");
        token.Type.Should().Be("token");
        token.Sensitivity.Should().Be(SensitivityLevel.Hidden);
        token.TokenLength.Should().Be(20);
    }

    [Fact]
    public async Task A_blueprint_token_field_with_a_length_below_16_is_listed_with_the_reason_and_not_applied()
    {
        var tenant = await TenantAsync();
        var client = await AdminInAsync(tenant);

        var listed = (await ListAsync(client)).Items.Single(i => i.Name == "shorttoken");
        listed.Errors.Should().ContainSingle().Which.Should().Contain("ClaimToken").And.Contain("16 to 128");

        (await client.PostAsync($"{Blueprints}/shorttoken", null, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StoredAsync(tenant, "short-ticket")).Should().BeNull();
    }
}

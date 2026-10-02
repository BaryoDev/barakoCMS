using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// <c>POST /api/me/switch</c> names its target with <c>tenant</c>, and <c>club</c> is an alias of it.
/// The endpoint hands out a token for another tenant, so every refusal is checked under both names.
/// </summary>
[Collection("Sequential")]
public class SwitchTenantRequestTests
{
    private const string NotAMember = "You are not a member of this tenant.";
    private const string TenantRequired = "A tenant is required.";
    private const string FieldsDisagree = "tenant and club name different tenants. Send one of them.";

    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public SwitchTenantRequestTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Switching_with_tenant_returns_a_token_for_that_tenant()
    {
        var (token, home, away) = await MemberOfTwoAsync();

        var resp = await SwitchAsync(token, home, new { tenant = away });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        TenantClaimOf(await TokenOfAsync(resp)).Should().Be(away);
    }

    [Fact]
    public async Task Switching_with_club_still_returns_a_token_for_that_tenant()
    {
        var (token, home, away) = await MemberOfTwoAsync();

        var resp = await SwitchAsync(token, home, new { club = away });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        TenantClaimOf(await TokenOfAsync(resp)).Should().Be(away);
    }

    [Fact]
    public async Task A_tenant_handle_is_trimmed_and_lowercased()
    {
        var (token, home, away) = await MemberOfTwoAsync();

        var resp = await SwitchAsync(token, home, new { tenant = $"  {away.ToUpperInvariant()} " });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        TenantClaimOf(await TokenOfAsync(resp)).Should().Be(away);
    }

    [Fact]
    public async Task The_token_presented_to_a_switch_by_tenant_stops_working()
    {
        var (token, home, away) = await MemberOfTwoAsync();
        (await MyTenantsAsync(token, home)).StatusCode.Should().Be(HttpStatusCode.OK,
            "the token works before the switch, so a 401 after it is the switch's doing");

        var resp = await SwitchAsync(token, home, new { tenant = away });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await MyTenantsAsync(token, home)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Tenant_and_club_naming_different_tenants_is_refused_and_switches_nowhere()
    {
        var (userId, username) = await CreateUserAsync();
        var home = await CreateTenantAsync();
        var first = await CreateTenantAsync();
        var second = await CreateTenantAsync();
        await GrantMembershipAsync(userId, home);
        await GrantMembershipAsync(userId, first);
        await GrantMembershipAsync(userId, second);
        var token = SignedToken(userId, username, home);

        var resp = await SwitchAsync(token, home, new { tenant = first, club = second });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        var errors = await ErrorsAsync(resp);
        errors.Should().HaveCount(1);
        errors[0].Name.Should().BeEquivalentTo("tenant");
        errors[0].Reason.Should().Be(FieldsDisagree);
        (await MyTenantsAsync(token, home)).StatusCode.Should().Be(HttpStatusCode.OK,
            "a refused switch must not revoke the token it was sent");
    }

    [Fact]
    public async Task Tenant_and_club_naming_the_same_tenant_is_accepted()
    {
        var (token, home, away) = await MemberOfTwoAsync();

        var resp = await SwitchAsync(token, home, new { tenant = $" {away.ToUpperInvariant()} ", club = away });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        TenantClaimOf(await TokenOfAsync(resp)).Should().Be(away);
    }

    [Fact]
    public async Task A_blank_club_beside_a_tenant_switches_to_the_tenant()
    {
        var (token, home, away) = await MemberOfTwoAsync();

        var resp = await SwitchAsync(token, home, new { tenant = away, club = " " });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        TenantClaimOf(await TokenOfAsync(resp)).Should().Be(away);
    }

    [Fact]
    public async Task A_blank_tenant_beside_a_club_switches_to_the_club()
    {
        var (token, home, away) = await MemberOfTwoAsync();

        var resp = await SwitchAsync(token, home, new { tenant = " ", club = away });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        TenantClaimOf(await TokenOfAsync(resp)).Should().Be(away);
    }

    [Theory]
    [InlineData("""{}""", "tenant")]
    [InlineData("""{"tenant":""}""", "tenant")]
    [InlineData("""{"tenant":"   "}""", "tenant")]
    [InlineData("""{"tenant":null,"club":null}""", "tenant")]
    [InlineData("""{"club":""}""", "club")]
    [InlineData("""{"tenant":"","club":" "}""", "club")]
    public async Task A_request_naming_no_tenant_is_refused(string json, string reportedAgainst)
    {
        var (token, home, _) = await MemberOfTwoAsync();

        var resp = await SwitchAsync(token, home, new StringContent(json, Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        var errors = await ErrorsAsync(resp);
        errors.Should().HaveCount(1);
        errors[0].Name.Should().BeEquivalentTo(reportedAgainst);
        errors[0].Reason.Should().Be(TenantRequired);
    }

    [Fact]
    public async Task A_tenant_the_caller_is_not_a_member_of_is_refused_the_same_way_under_either_name()
    {
        var (userId, username) = await CreateUserAsync();
        var home = await CreateTenantAsync();
        var someoneElses = await CreateTenantAsync();
        await GrantMembershipAsync(userId, home);
        var token = SignedToken(userId, username, home);

        var byTenant = await SwitchAsync(token, home, new { tenant = someoneElses });
        var byClub = await SwitchAsync(token, home, new { club = someoneElses });

        await ShouldBeRefusedAsNotAMemberAsync(byTenant, "tenant");
        await ShouldBeRefusedAsNotAMemberAsync(byClub, "club");
        (await MyTenantsAsync(token, home)).StatusCode.Should().Be(HttpStatusCode.OK,
            "neither refusal may revoke the token it was sent");
    }

    [Fact]
    public async Task An_inactive_tenant_is_refused_the_same_way_under_either_name()
    {
        var (userId, username) = await CreateUserAsync();
        var home = await CreateTenantAsync();
        var closed = await CreateTenantAsync(isActive: false);
        await GrantMembershipAsync(userId, home);
        await GrantMembershipAsync(userId, closed);
        var token = SignedToken(userId, username, home);

        var byTenant = await SwitchAsync(token, home, new { tenant = closed });
        var byClub = await SwitchAsync(token, home, new { club = closed });

        await ShouldBeRefusedAsNotAMemberAsync(byTenant, "tenant");
        await ShouldBeRefusedAsNotAMemberAsync(byClub, "club");
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("club")]
    public async Task Switching_without_a_token_is_401(string field)
    {
        var (_, home, away) = await MemberOfTwoAsync();

        var resp = await SwitchAsync(bearer: null, home, new Dictionary<string, string> { [field] = away });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_OpenAPI_document_lists_tenant_and_club_on_the_switch_request()
    {
        using var doc = await OpenApiTagTests.FetchDocumentAsync(_factory);
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        var content = doc.RootElement.GetProperty("paths").GetProperty("/api/me/switch").GetProperty("post")
            .GetProperty("requestBody").GetProperty("content").EnumerateObject().ToList();
        content.Should().NotBeEmpty("the switch takes a body");

        var properties = SchemaProperties(content[0].Value.GetProperty("schema"), schemas).ToList();

        properties.Should().HaveCount(2);
        properties.Should().BeEquivalentTo("tenant", "club");
    }

    private static IEnumerable<string> SchemaProperties(JsonElement schema, JsonElement schemas)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            schema = schemas.GetProperty(reference.GetString()!.Split('/')[^1]);
        }

        if (schema.TryGetProperty("properties", out var properties))
        {
            foreach (var property in properties.EnumerateObject())
            {
                yield return property.Name;
            }
        }

        foreach (var keyword in new[] { "allOf", "oneOf" })
        {
            if (!schema.TryGetProperty(keyword, out var parts))
            {
                continue;
            }

            foreach (var name in parts.EnumerateArray().SelectMany(part => SchemaProperties(part, schemas)))
            {
                yield return name;
            }
        }
    }

    private static async Task ShouldBeRefusedAsNotAMemberAsync(HttpResponseMessage resp, string reportedAgainst)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        var errors = await ErrorsAsync(resp);
        errors.Should().HaveCount(1);
        errors[0].Name.Should().BeEquivalentTo(reportedAgainst);
        errors[0].Reason.Should().Be(NotAMember);
    }

    private static async Task<List<(string Name, string Reason)>> ErrorsAsync(HttpResponseMessage resp)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.TryGetProperty("errors", out var errors)
            .Should().BeTrue("ProblemDetails carries its entries under 'errors'");

        return errors.EnumerateArray()
            .Select(e => (e.GetProperty("name").GetString() ?? "", e.GetProperty("reason").GetString() ?? ""))
            .ToList();
    }

    private static async Task<string> TokenOfAsync(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<barakoCMS.Features.Me.SwitchTenantResponse>())!.Token;

    private static string? TenantClaimOf(string jwt) =>
        new JwtSecurityTokenHandler().ReadJwtToken(jwt).Claims
            .FirstOrDefault(c => c.Type == "tenant")?.Value;

    private Task<HttpResponseMessage> SwitchAsync(string? bearer, string fromTenant, object body) =>
        SwitchAsync(bearer, fromTenant, JsonContent.Create(body));

    private async Task<HttpResponseMessage> SwitchAsync(string? bearer, string fromTenant, HttpContent body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/me/switch") { Content = body };
        req.Headers.Add("X-Tenant", fromTenant);
        if (bearer is not null)
            req.Headers.Authorization = new("Bearer", bearer);
        return await _client.SendAsync(req);
    }

    private async Task<HttpResponseMessage> MyTenantsAsync(string bearer, string tenant)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/me/tenants");
        req.Headers.Add("X-Tenant", tenant);
        req.Headers.Authorization = new("Bearer", bearer);
        return await _client.SendAsync(req);
    }

    /// <summary>A user who belongs to two tenants, holding a token for the first.</summary>
    private async Task<(string Token, string Home, string Away)> MemberOfTwoAsync()
    {
        var (userId, username) = await CreateUserAsync();
        var home = await CreateTenantAsync();
        var away = await CreateTenantAsync();
        await GrantMembershipAsync(userId, home);
        await GrantMembershipAsync(userId, away);
        return (SignedToken(userId, username, home), home, away);
    }

    private async Task<(Guid Id, string Username)> CreateUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var username = $"switch-{Guid.NewGuid():N}";
        var id = Guid.NewGuid();
        session.Store(new User
        {
            Id = id,
            Username = username,
            Email = $"{username}@test.com",
            PasswordHash = "not used: these tests sign their own token",
            RoleIds = new List<Guid>(),
        });
        await session.SaveChangesAsync();
        return (id, username);
    }

    private async Task<string> CreateTenantAsync(bool isActive = true)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var slug = $"switch-{Guid.NewGuid():N}";
        session.Store(new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            Name = slug,
            IsActive = isActive,
        });
        await session.SaveChangesAsync();
        return slug;
    }

    private async Task GrantMembershipAsync(Guid userId, string tenantSlug)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new Membership
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TenantSlug = tenantSlug,
            Status = MembershipStatus.Active,
            RoleIds = new List<Guid>(),
        });
        await session.SaveChangesAsync();
    }

    /// <summary>
    /// A token signed like the ones the API mints. Signing in would do, but the auth endpoints allow
    /// five attempts per address and every test here shares one.
    /// </summary>
    private static string SignedToken(Guid userId, string username, string tenant)
    {
        var key = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(IntegrationTestFixture.JwtKey));
        var claims = new List<System.Security.Claims.Claim>
        {
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("UserId", userId.ToString()),
            new("Username", username),
            new("tenant", tenant),
            new(System.Security.Claims.ClaimTypes.Role, "User"),
        };
        var token = new JwtSecurityToken(
            issuer: "BarakoTest",
            audience: "BarakoClient",
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new Microsoft.IdentityModel.Tokens.SigningCredentials(
                key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

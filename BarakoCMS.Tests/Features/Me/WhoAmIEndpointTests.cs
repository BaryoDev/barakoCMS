using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.Me;

/// <summary>
/// <c>GET /api/me</c> answers the caller's own stored roles and capabilities in the tenant the
/// session acts in, the reading the API decides sensitivity by (#1107).
/// </summary>
/// <remarks>
/// Every role here has a name no gate lists and the token claims a different name, so the answer
/// can only have come from the store.
/// </remarks>
[Collection("Sequential")]
public class WhoAmIEndpointTests
{
    private const string Route = "/api/me";

    private readonly IntegrationTestFixture _factory;

    public WhoAmIEndpointTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Role> RoleAsync(params string[] capabilities)
    {
        var role = new Role { Id = Guid.NewGuid(), Name = $"Me Role {Guid.NewGuid():N}", SystemCapabilities = capabilities.ToList() };
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(role);
        await session.SaveChangesAsync(Ct);
        return role;
    }

    private async Task<string> TenantAsync()
    {
        var slug = $"me-{Guid.NewGuid():N}"[..14];
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    /// <summary>A stored user with these global roles and one membership role in each tenant named.</summary>
    private async Task<Guid> UserAsync(Role[] global, params (string Tenant, Role Role)[] memberships)
    {
        var userId = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new User
        {
            Id = userId,
            Username = $"me-{userId:n}",
            Email = $"me-{userId:n}@example.com",
            RoleIds = global.Select(r => r.Id).ToList(),
        });
        foreach (var (tenant, role) in memberships)
        {
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TenantSlug = tenant,
                Status = MembershipStatus.Active,
                RoleIds = [role.Id],
            });
        }

        await session.SaveChangesAsync(Ct);
        return userId;
    }

    private HttpClient Client(Guid userId, string? tokenTenant = null, string? header = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(
                roles: ["Claimed Only"],
                userId: userId.ToString(),
                additionalClaims: tokenTenant is null ? null : new Dictionary<string, string> { ["tenant"] = tokenTenant }));
        if (header is not null)
            client.DefaultRequestHeaders.Add("X-Tenant", header);
        return client;
    }

    private static async Task<JsonElement> MeAsync(HttpClient client, string route = Route)
    {
        var response = await client.GetAsync(route, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var parsed = JsonDocument.Parse(body);
        return parsed.RootElement.Clone();
    }

    private static List<string> Capabilities(JsonElement me) =>
        me.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()!).ToList();

    private static List<Guid> RoleIds(JsonElement me) =>
        me.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToList();

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        var response = await _factory.CreateClient().GetAsync(Route, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_custom_role_holding_view_sensitive_is_reported_with_it()
    {
        var nurse = await RoleAsync(SystemCapabilities.ViewSensitive, SystemCapabilities.ManageQueries);
        var userId = await UserAsync([nurse]);

        var me = await MeAsync(Client(userId));

        me.GetProperty("userId").GetGuid().Should().Be(userId);
        me.GetProperty("username").GetString().Should().Be($"me-{userId:n}");

        var roles = me.GetProperty("roles").EnumerateArray().ToList();
        roles.Should().HaveCount(1);
        roles[0].GetProperty("id").GetGuid().Should().Be(nurse.Id);
        roles[0].GetProperty("name").GetString().Should().Be(nurse.Name, "the stored name, not the token's claim");

        Capabilities(me).Should().Equal(
            new[] { SystemCapabilities.ManageQueries, SystemCapabilities.ViewSensitive },
            "every capability the role carries, sorted");
    }

    [Fact]
    public async Task The_wildcard_is_reported_as_stored_and_repeats_are_dropped()
    {
        var everything = await RoleAsync(SystemCapabilities.All, SystemCapabilities.ViewHidden);
        var auditor = await RoleAsync(SystemCapabilities.ViewHidden);
        var userId = await UserAsync([everything, auditor]);

        var me = await MeAsync(Client(userId));

        RoleIds(me).Should().BeEquivalentTo(new[] { everything.Id, auditor.Id });
        Capabilities(me).Should().Equal(SystemCapabilities.All, SystemCapabilities.ViewHidden);
    }

    [Fact]
    public async Task A_membership_in_one_tenant_does_not_report_its_roles_under_another()
    {
        var clinic = await TenantAsync();
        var school = await TenantAsync();
        var nurse = await RoleAsync(SystemCapabilities.ViewSensitive);
        var reader = await RoleAsync();
        var userId = await UserAsync([], (clinic, nurse), (school, reader));

        var atClinic = await MeAsync(Client(userId, clinic, clinic));
        atClinic.GetProperty("tenant").GetString().Should().Be(clinic);
        RoleIds(atClinic).Should().Equal(nurse.Id);
        Capabilities(atClinic).Should().Equal(SystemCapabilities.ViewSensitive);

        var atSchool = await MeAsync(Client(userId, school, school));
        atSchool.GetProperty("tenant").GetString().Should().Be(school);
        RoleIds(atSchool).Should().Equal(reader.Id);
        Capabilities(atSchool).Should().BeEmpty("the role from the clinic membership is not held in the school");
    }

    [Fact]
    public async Task The_tokens_tenant_decides_not_an_X_Tenant_header_naming_another()
    {
        var clinic = await TenantAsync();
        var school = await TenantAsync();
        var nurse = await RoleAsync(SystemCapabilities.ViewSensitive);
        var reader = await RoleAsync();
        var userId = await UserAsync([], (clinic, nurse), (school, reader));

        var me = await MeAsync(Client(userId, tokenTenant: school, header: clinic));

        me.GetProperty("tenant").GetString().Should().Be(school);
        RoleIds(me).Should().Equal(reader.Id);
        Capabilities(me).Should().BeEmpty("a session for the school does not act in the clinic");
    }

    [Fact]
    public async Task No_query_parameter_selects_another_user()
    {
        var other = await RoleAsync(SystemCapabilities.ViewHidden);
        var otherId = await UserAsync([other]);
        var mine = await RoleAsync(SystemCapabilities.ViewAuditLog);
        var userId = await UserAsync([mine]);

        var me = await MeAsync(Client(userId),
            $"{Route}?userId={otherId}&id={otherId}&username=me-{otherId:n}");

        me.GetProperty("userId").GetGuid().Should().Be(userId);
        RoleIds(me).Should().Equal(mine.Id);
        Capabilities(me).Should().Equal(SystemCapabilities.ViewAuditLog);
    }

    [Fact]
    public async Task An_API_key_is_refused_with_403()
    {
        var role = await RoleAsync(SystemCapabilities.ViewSensitive);
        var ownerId = await UserAsync([role]);
        var secret = "bcms_" + Guid.NewGuid().ToString("N");
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ApiKey
            {
                Id = Guid.NewGuid(),
                Name = "me",
                KeyHash = ApiKeyService.Hash(secret),
                Prefix = secret[..12],
                UserId = ownerId,
                Scopes = ["content:read"],
            });
            await session.SaveChangesAsync(Ct);
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);

        var response = await client.GetAsync(Route, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Ct)).Should().NotContain(role.Id.ToString());
    }

    [Fact]
    public async Task The_OpenAPI_document_lists_the_route_under_the_Me_tag()
    {
        using var doc = await OpenApiTagTests.FetchDocumentAsync(_factory);

        var operation = OpenApiSchemaReader.Operation(doc, Route, "get");

        operation.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).Should().Equal("Me");
        OpenApiSchemaReader.PropertyNames(OpenApiSchemaReader.OkResponse(doc, operation))
            .Should().BeEquivalentTo(["userId", "username", "tenant", "roles", "capabilities"]);
    }

    [Fact]
    public async Task The_answer_is_sent_no_store()
    {
        var userId = await UserAsync([await RoleAsync()]);

        var response = await Client(userId).GetAsync(Route, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }
}

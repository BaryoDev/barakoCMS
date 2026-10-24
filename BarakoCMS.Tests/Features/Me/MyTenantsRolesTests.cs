using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.Me;

/// <summary>
/// <c>GET /api/me/tenants</c> reports the roles the caller holds in each tenant (#677), since a role
/// can differ per tenant and a switcher shows it beside each one.
/// </summary>
[Collection("Integration1")]
public class MyTenantsRolesTests
{
    private const string Route = "/api/me/tenants";

    private readonly IntegrationTestFixture _factory;

    public MyTenantsRolesTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Role> RoleAsync(string prefix, string? tenantSlug = null)
    {
        var role = new Role { Id = Guid.NewGuid(), Name = $"{prefix} {Guid.NewGuid():N}", TenantSlug = tenantSlug };
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(role);
        await session.SaveChangesAsync(Ct);
        return role;
    }

    private async Task<string> TenantAsync()
    {
        var slug = $"mt-{Guid.NewGuid():N}"[..14];
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, Status = TenantStatus.Active });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    private async Task<Guid> UserAsync(Role[] global, params (string Tenant, Role Role, MembershipStatus Status)[] memberships)
    {
        var userId = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new User
        {
            Id = userId,
            Username = $"mt-{userId:n}",
            Email = $"mt-{userId:n}@example.com",
            RoleIds = global.Select(r => r.Id).ToList(),
        });
        foreach (var (tenant, role, status) in memberships)
        {
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TenantSlug = tenant,
                Status = status,
                RoleIds = [role.Id],
            });
        }

        await session.SaveChangesAsync(Ct);
        return userId;
    }

    private async Task<Dictionary<string, List<string>>> RolesByTenantAsync(Guid userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: ["Claimed Only"], userId: userId.ToString()));

        var response = await client.GetAsync($"{Route}?pageSize=100", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("items").EnumerateArray().ToDictionary(
            t => t.GetProperty("slug").GetString()!,
            t => t.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("name").GetString()!).ToList());
    }

    [Fact]
    public async Task Each_tenant_carries_the_roles_held_there_and_the_global_ones()
    {
        var shop = await TenantAsync();
        var school = await TenantAsync();
        var everywhere = await RoleAsync("B Global");
        var clerk = await RoleAsync("A Clerk");
        var teacher = await RoleAsync("C Teacher");
        var userId = await UserAsync(
            [everywhere],
            (shop, clerk, MembershipStatus.Active),
            (school, teacher, MembershipStatus.Active));

        var roles = await RolesByTenantAsync(userId);

        roles.Should().HaveCount(2);
        roles[shop].Should().Equal([clerk.Name, everywhere.Name], "sorted by name, and the school's role is not held here");
        roles[school].Should().Equal([everywhere.Name, teacher.Name]);
    }

    [Fact]
    public async Task A_suspended_membership_lists_neither_the_tenant_nor_its_role()
    {
        var shop = await TenantAsync();
        var school = await TenantAsync();
        var clerk = await RoleAsync("Clerk");
        var teacher = await RoleAsync("Teacher");
        var userId = await UserAsync(
            [],
            (shop, clerk, MembershipStatus.Active),
            (school, teacher, MembershipStatus.Suspended));

        var roles = await RolesByTenantAsync(userId);

        roles.Keys.Should().Equal(shop);
        roles[shop].Should().Equal(clerk.Name);
    }

    [Fact]
    public async Task A_role_another_tenant_owns_is_not_listed_where_it_grants_nothing()
    {
        var shop = await TenantAsync();
        var school = await TenantAsync();
        var clerk = await RoleAsync("Clerk", shop);
        var teacher = await RoleAsync("Teacher", school);
        var userId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User { Id = userId, Username = $"mt-{userId:n}", Email = $"mt-{userId:n}@example.com" });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(), UserId = userId, TenantSlug = shop, Status = MembershipStatus.Active, RoleIds = [clerk.Id, teacher.Id],
            });
            await session.SaveChangesAsync(Ct);
        }

        var roles = await RolesByTenantAsync(userId);

        roles[shop].Should().Equal([clerk.Name], "the school's role grants nothing in the shop, the rule MembershipRoles applies");
    }

    [Fact]
    public async Task The_OpenAPI_document_lists_roles_on_each_tenant()
    {
        using var doc = await OpenApiTagTests.FetchDocumentAsync(_factory);

        var page = OpenApiSchemaReader.OkResponse(doc, OpenApiSchemaReader.Operation(doc, Route, "get"));
        var items = page.GetProperty("properties").GetProperty("items").GetProperty("items");
        OpenApiSchemaReader.PropertyNames(OpenApiSchemaReader.Resolve(doc, items))
            .Should().Contain(["slug", "name", "roles"]);
    }
}

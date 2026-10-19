using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BarakoCMS.ExternalAuth;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.ExternalAuth;

/// <summary>
/// A tenant's claim-to-role map (#915): what an id token's claim gives a person in that tenant,
/// and who may set the map.
/// </summary>
/// <remarks>
/// Sign-in goes through <see cref="SocialSignIn.IssueForIdentityAsync"/> with an identity the way
/// the callback builds one after the token has been validated, so these tests are about what the
/// map does and not about the token checks, which <c>OidcSignInTests</c> covers. Every grant is
/// paired with the same sign-in under a claim that matches nothing, or in a tenant with no map,
/// because a sign-in that always granted the role would pass the grant on its own.
/// </remarks>
[Collection("Sequential")]
public class ExternalRoleMapTests
{
    private static int _ipCounter;
    private readonly IntegrationTestFixture _fixture;

    public ExternalRoleMapTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20].ToLowerInvariant();

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Groups(params string[] values) =>
        new Dictionary<string, IReadOnlyList<string>> { ["groups"] = values };

    private async Task StoreAsync(params object[] documents)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        foreach (var document in documents) session.Store(document);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<(string Slug, Role Teacher)> TenantWithMapAsync(string claim = "groups", string value = "staff")
    {
        var slug = Unique("map");
        var teacher = new Role
        {
            Id = Guid.NewGuid(), Name = Unique("Teacher"), TenantSlug = slug,
            Permissions = [], SystemCapabilities = [],
        };
        await StoreAsync(
            new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug },
            teacher,
            new ExternalRoleMap
            {
                Id = slug, Claim = claim,
                Rules = [new ExternalRoleRule { Value = value, RoleId = teacher.Id }],
            });
        return (slug, teacher);
    }

    private async Task<User> PersonAsync()
    {
        var user = new User
        {
            Id = Guid.NewGuid(), Email = $"{Unique("p")}@example.com", Username = Unique("u"), PasswordHash = "",
        };
        await StoreAsync(user);
        return user;
    }

    private async Task<SocialSignIn.Tokens> SignInAsync(
        User user, string club, IReadOnlyDictionary<string, IReadOnlyList<string>> claims)
    {
        using var scope = _fixture.Services.CreateScope();
        var services = scope.ServiceProvider;
        var session = services.GetRequiredService<IDocumentSession>();
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Headers["User-Agent"] = "role-map-tests";

        var (tokens, _) = await SocialSignIn.IssueForIdentityAsync(
            session,
            services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
            services.GetRequiredService<barakoCMS.Core.Interfaces.IDeviceGate>(),
            services.GetRequiredService<barakoCMS.Infrastructure.Auth.ITokenIssuer>(),
            services.GetRequiredService<barakoCMS.Infrastructure.Auth.Mfa.IMfaService>(),
            context,
            new OidcIdentity("https://idp.test.example", "sub-" + Guid.NewGuid().ToString("N"), user.Email, true, null, null)
            {
                Claims = claims,
            },
            "stub",
            club,
            Ct);
        return tokens;
    }

    private async Task<List<Membership>> MembershipsAsync(Guid userId, string slug)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.Query<Membership>().Where(m => m.UserId == userId && m.TenantSlug == slug).ToListAsync(Ct)).ToList();
    }

    [Fact]
    public async Task A_claim_the_tenant_mapped_gives_that_role_in_the_tenants_membership()
    {
        var (slug, teacher) = await TenantWithMapAsync();
        var user = await PersonAsync();

        var tokens = await SignInAsync(user, slug, Groups("everyone", "staff"));

        var memberships = await MembershipsAsync(user.Id, slug);
        memberships.Should().HaveCount(1);
        memberships[0].RoleIds.Should().Equal(teacher.Id);
        memberships[0].Status.Should().Be(MembershipStatus.Active);
        tokens.Allowed.Should().BeTrue("the membership the claim made is what lets the issuer mint for the tenant");
    }

    [Fact]
    public async Task A_claim_the_map_does_not_name_gives_nothing()
    {
        var (slug, _) = await TenantWithMapAsync();
        var user = await PersonAsync();

        var tokens = await SignInAsync(user, slug, Groups("everyone", "Staff"));

        (await MembershipsAsync(user.Id, slug)).Should().BeEmpty("a value matches exactly, so Staff is not staff");
        tokens.Allowed.Should().BeFalse("without a membership the tenant is still closed to them");
    }

    [Fact]
    public async Task The_map_adds_to_the_roles_a_member_has_and_a_second_sign_in_adds_nothing_more()
    {
        var (slug, teacher) = await TenantWithMapAsync();
        var user = await PersonAsync();
        var other = Guid.NewGuid();
        await StoreAsync(
            new Role { Id = other, Name = Unique("Other"), TenantSlug = slug, Permissions = [], SystemCapabilities = [] },
            new Membership
            {
                Id = Guid.NewGuid(), UserId = user.Id, TenantSlug = slug,
                Status = MembershipStatus.Active, RoleIds = [other],
            });

        await SignInAsync(user, slug, Groups("staff"));
        await SignInAsync(user, slug, Groups("staff"));

        var memberships = await MembershipsAsync(user.Id, slug);
        memberships.Should().HaveCount(1);
        memberships[0].RoleIds.Should().BeEquivalentTo([other, teacher.Id]);
        memberships[0].RoleIds.Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData(MembershipStatus.Removed)]
    [InlineData(MembershipStatus.Suspended)]
    public async Task A_claim_does_not_bring_back_a_member_an_administrator_took_out(MembershipStatus status)
    {
        var (slug, teacher) = await TenantWithMapAsync();
        var user = await PersonAsync();
        await StoreAsync(new Membership
        {
            Id = Guid.NewGuid(), UserId = user.Id, TenantSlug = slug, Status = status, RoleIds = [],
        });

        var tokens = await SignInAsync(user, slug, Groups("staff"));

        var memberships = await MembershipsAsync(user.Id, slug);
        memberships.Should().HaveCount(1);
        memberships[0].Status.Should().Be(status);
        memberships[0].RoleIds.Should().NotContain(teacher.Id);
        tokens.Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task A_role_the_tenant_cannot_grant_is_not_given_even_if_a_stored_map_names_it()
    {
        var (slug, _) = await TenantWithMapAsync();
        var elsewhere = Guid.NewGuid();
        await StoreAsync(
            new Role { Id = elsewhere, Name = Unique("Foreign"), TenantSlug = Unique("other"), Permissions = [], SystemCapabilities = [] },
            new ExternalRoleMap
            {
                Id = slug, Claim = "groups",
                Rules =
                [
                    new ExternalRoleRule { Value = "staff", RoleId = SystemRoles.SuperAdminRoleId },
                    new ExternalRoleRule { Value = "staff", RoleId = elsewhere },
                ],
            });
        var user = await PersonAsync();

        await SignInAsync(user, slug, Groups("staff"));

        (await MembershipsAsync(user.Id, slug)).Should().BeEmpty("neither SuperAdmin nor another tenant's role may come from a claim");
    }

    [Fact]
    public async Task One_tenants_map_does_not_apply_in_another_tenant()
    {
        var (_, _) = await TenantWithMapAsync();
        var plain = Unique("plain");
        await StoreAsync(new Tenant { Id = Guid.NewGuid(), Slug = plain, Name = plain });
        var user = await PersonAsync();

        await SignInAsync(user, plain, Groups("staff"));

        (await MembershipsAsync(user.Id, plain)).Should().BeEmpty();
    }

    [Fact]
    public async Task Each_tenant_maps_the_same_claim_to_its_own_role()
    {
        var (a, teacherA) = await TenantWithMapAsync(claim: "groups", value: "staff");
        var (b, teacherB) = await TenantWithMapAsync(claim: "groups", value: "staff");
        var user = await PersonAsync();

        await SignInAsync(user, a, Groups("staff"));
        await SignInAsync(user, b, Groups("staff"));

        (await MembershipsAsync(user.Id, a)).Single().RoleIds.Should().Equal(teacherA.Id);
        (await MembershipsAsync(user.Id, b)).Single().RoleIds.Should().Equal(teacherB.Id);
    }

    // ---- the map endpoints ----

    /// <summary>
    /// A caller who holds <paramref name="role"/> in <paramref name="slug"/> through a membership
    /// and nowhere else, or no token at all.
    /// </summary>
    private HttpClient ClientFor(string slug, string? role)
    {
        var client = _fixture.CreateClient();
        if (role is not null)
        {
            var user = new User { Id = Guid.NewGuid(), Email = $"{Unique("c")}@example.com", Username = Unique("c"), PasswordHash = "" };
            var roleId = role == "Admin" ? SystemRoles.AdminRoleId : SystemRoles.UserRoleId;
            StoreAsync(
                user,
                new Membership
                {
                    Id = Guid.NewGuid(), UserId = user.Id, TenantSlug = slug,
                    Status = MembershipStatus.Active, RoleIds = [roleId],
                }).GetAwaiter().GetResult();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                _fixture.CreateToken(
                    roles: [role],
                    userId: user.Id.ToString(),
                    additionalClaims: new Dictionary<string, string> { ["tenant"] = slug, ["Username"] = user.Username }));
        }

        client.DefaultRequestHeaders.Add("X-Tenant", slug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"198.51.100.{Interlocked.Increment(ref _ipCounter) % 250 + 1}");
        return client;
    }

    private async Task<string> AdminTenantAsync()
    {
        var slug = Unique("adm");
        await StoreAsync(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
        return slug;
    }

    [Fact]
    public async Task A_tenant_administrator_sets_reads_and_removes_the_map()
    {
        var slug = await AdminTenantAsync();
        var role = new Role { Id = Guid.NewGuid(), Name = Unique("Teacher"), TenantSlug = slug, Permissions = [], SystemCapabilities = [] };
        await StoreAsync(role);
        var client = ClientFor(slug, "Admin");

        var put = await client.PutAsJsonAsync("/api/tenants/external-role-map",
            new { claim = "groups", rules = new[] { new { value = "staff", roleId = role.Id } } }, Ct);
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync(Ct));

        var get = await client.GetAsync("/api/tenants/external-role-map", Ct);
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        (await get.Content.ReadAsStringAsync(Ct)).Should().Contain(role.Id.ToString());

        (await client.DeleteAsync("/api/tenants/external-role-map", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync("/api/tenants/external-role-map", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_map_cannot_name_SuperAdmin_or_a_role_of_another_tenant()
    {
        var slug = await AdminTenantAsync();
        var foreign = new Role { Id = Guid.NewGuid(), Name = Unique("Foreign"), TenantSlug = Unique("other"), Permissions = [], SystemCapabilities = [] };
        await StoreAsync(foreign);
        var client = ClientFor(slug, "Admin");

        foreach (var roleId in new[] { SystemRoles.SuperAdminRoleId, foreign.Id, Guid.NewGuid() })
        {
            var response = await client.PutAsJsonAsync("/api/tenants/external-role-map",
                new { claim = "groups", rules = new[] { new { value = "staff", roleId } } }, Ct);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"role {roleId} is not one a claim may grant");
        }

        (await client.GetAsync("/api/tenants/external-role-map", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_caller_without_the_capability_or_without_a_token_cannot_reach_the_map()
    {
        var (slug, _) = await TenantWithMapAsync();

        (await ClientFor(slug, null).GetAsync("/api/tenants/external-role-map", Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ClientFor(slug, "User").GetAsync("/api/tenants/external-role-map", Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ClientFor(slug, "User").PutAsJsonAsync("/api/tenants/external-role-map",
            new { claim = "groups", rules = Array.Empty<object>() }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ClientFor(slug, "User").DeleteAsync("/api/tenants/external-role-map", Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var scope = _fixture.Services.CreateScope();
        var map = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<ExternalRoleMap>(slug, Ct);
        map.Should().NotBeNull("the refused delete left the map alone");
    }

    [Fact]
    public async Task An_administrator_of_one_tenant_reads_only_their_own_tenants_map()
    {
        var (withMap, _) = await TenantWithMapAsync();
        var other = await AdminTenantAsync();

        (await ClientFor(other, "Admin").GetAsync("/api/tenants/external-role-map", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "the route reads the caller's tenant, never one named by the request");
        (await ClientFor(withMap, "Admin").GetAsync("/api/tenants/external-role-map", Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_role_with_no_owner_is_the_default_tenants_and_a_claim_cannot_grant_it_elsewhere()
    {
        var (slug, _) = await TenantWithMapAsync();
        var unowned = Guid.NewGuid();
        await StoreAsync(
            new Role { Id = unowned, Name = Unique("Unowned"), TenantSlug = null, Permissions = [], SystemCapabilities = [] },
            new ExternalRoleMap
            {
                Id = slug, Claim = "groups",
                Rules = [new ExternalRoleRule { Value = "staff", RoleId = unowned }],
            });
        var user = await PersonAsync();

        await SignInAsync(user, slug, Groups("staff"));

        (await MembershipsAsync(user.Id, slug)).Should().BeEmpty();
        var put = await ClientFor(slug, "Admin").PutAsJsonAsync("/api/tenants/external-role-map",
            new { claim = "groups", rules = new[] { new { value = "staff", roleId = unowned } } }, Ct);
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_role_carrying_a_platform_capability_is_not_given_by_a_claim()
    {
        var (slug, _) = await TenantWithMapAsync();
        var powerful = new Role
        {
            Id = Guid.NewGuid(), Name = Unique("Power"), TenantSlug = slug,
            Permissions = [], SystemCapabilities = [SystemCapabilities.ManageRoles],
        };
        await StoreAsync(powerful, new ExternalRoleMap
        {
            Id = slug, Claim = "groups",
            Rules = [new ExternalRoleRule { Value = "staff", RoleId = powerful.Id }],
        });
        var user = await PersonAsync();

        await SignInAsync(user, slug, Groups("staff"));

        (await MembershipsAsync(user.Id, slug)).Should().BeEmpty();
        (await ClientFor(slug, "Admin").PutAsJsonAsync("/api/tenants/external-role-map",
            new { claim = "groups", rules = new[] { new { value = "staff", roleId = powerful.Id } } }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- the link endpoints ----

    private async Task<(string Slug, User Member, ExternalIdentity Link)> MemberWithLinkAsync()
    {
        var slug = await AdminTenantAsync();
        var member = await PersonAsync();
        var link = new ExternalIdentity
        {
            Id = ExternalIdentity.KeyOf("https://idp.test.example", "s-" + member.Id.ToString("N")),
            Issuer = "https://idp.test.example", Subject = "s-" + member.Id.ToString("N"),
            UserId = member.Id, Provider = "stub",
        };
        await StoreAsync(link, new Membership
        {
            Id = Guid.NewGuid(), UserId = member.Id, TenantSlug = slug,
            Status = MembershipStatus.Active, RoleIds = [SystemRoles.UserRoleId],
        });
        return (slug, member, link);
    }

    [Fact]
    public async Task An_administrator_lists_and_removes_a_members_provider_links()
    {
        var (slug, member, link) = await MemberWithLinkAsync();
        var client = ClientFor(slug, "Admin");
        var route = $"/api/tenants/members/{member.Id}/external-identities";

        var list = await client.GetAsync(route, Ct);
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await list.Content.ReadAsStringAsync(Ct);
        body.Should().Contain(link.Id);
        body.Should().NotContain(link.Subject, "the provider's subject is not shown");

        (await client.DeleteAsync($"{route}/{link.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _fixture.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<ExternalIdentity>(link.Id, Ct))
            .Should().BeNull();
    }

    [Fact]
    public async Task The_link_routes_answer_401_without_a_token_and_403_without_the_capability()
    {
        var (slug, member, link) = await MemberWithLinkAsync();
        var route = $"/api/tenants/members/{member.Id}/external-identities";

        (await ClientFor(slug, null).GetAsync(route, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ClientFor(slug, "User").GetAsync(route, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ClientFor(slug, "User").DeleteAsync($"{route}/{link.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var scope = _fixture.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<ExternalIdentity>(link.Id, Ct))
            .Should().NotBeNull("the refused delete left the link alone");
    }

    [Fact]
    public async Task An_administrator_cannot_see_or_remove_the_links_of_somebody_outside_their_tenant()
    {
        var (_, member, link) = await MemberWithLinkAsync();
        var otherTenant = await AdminTenantAsync();
        var client = ClientFor(otherTenant, "Admin");
        var route = $"/api/tenants/members/{member.Id}/external-identities";

        (await client.GetAsync(route, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.DeleteAsync($"{route}/{link.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var scope = _fixture.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<ExternalIdentity>(link.Id, Ct))
            .Should().NotBeNull();
    }

    [Fact]
    public async Task A_link_is_removed_only_through_the_user_it_belongs_to()
    {
        var (slug, _, link) = await MemberWithLinkAsync();
        var bystander = await PersonAsync();
        await StoreAsync(new Membership
        {
            Id = Guid.NewGuid(), UserId = bystander.Id, TenantSlug = slug,
            Status = MembershipStatus.Active, RoleIds = [SystemRoles.UserRoleId],
        });

        var response = await ClientFor(slug, "Admin")
            .DeleteAsync($"/api/tenants/members/{bystander.Id}/external-identities/{link.Id}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var scope = _fixture.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<ExternalIdentity>(link.Id, Ct))
            .Should().NotBeNull();
    }
}

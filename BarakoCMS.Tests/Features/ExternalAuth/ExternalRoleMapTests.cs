using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BarakoCMS.ExternalAuth;
using FluentAssertions;
using Marten;
using Marten.Patching;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
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
    private static readonly Lock Gate = new();
    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? _host;
    private readonly IntegrationTestFixture _fixture;

    public ExternalRoleMapTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        lock (Gate)
        {
            _host ??= fixture.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Oidc:Providers:stub:Authority"] = OidcStubProvider.Authority,
                    ["Oidc:Providers:stub:ClientId"] = OidcStubProvider.ClientId,
                    ["Oidc:Providers:stub:ClientSecret"] = OidcStubProvider.ClientSecret,
                    ["Oidc:Providers:alpha:Authority"] = OidcStubProvider.Authority,
                    ["Oidc:Providers:alpha:ClientId"] = OidcStubProvider.ClientId,
                    ["Oidc:Providers:alpha:ClientSecret"] = OidcStubProvider.ClientSecret,
                    ["Oidc:Providers:beta:Authority"] = OidcStubProvider.Authority,
                    ["Oidc:Providers:beta:ClientId"] = OidcStubProvider.ClientId,
                    ["Oidc:Providers:beta:ClientSecret"] = OidcStubProvider.ClientSecret,
                    ["Oidc:Providers:entra:Authority"] = OidcStubProvider.SharedAuthority,
                    ["Oidc:Providers:entra:Issuer"] = OidcStubProvider.SharedIssuerTemplate,
                    ["Oidc:Providers:entra:ClientId"] = OidcStubProvider.ClientId,
                    ["Oidc:Providers:entra:ClientSecret"] = OidcStubProvider.ClientSecret,
                })));
        }
    }

    private sealed class WithResolver(IServiceProvider inner, barakoCMS.Infrastructure.Services.IPermissionResolver resolver)
        : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(barakoCMS.Infrastructure.Services.IPermissionResolver) ? resolver : inner.GetService(serviceType);
    }

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
                Rules = [new ExternalRoleRule { Provider = "stub", Value = value, RoleId = teacher.Id }],
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
        User user, string club, IReadOnlyDictionary<string, IReadOnlyList<string>> claims, string provider = "stub",
        string issuer = "https://idp.test.example",
        barakoCMS.Infrastructure.Services.IPermissionResolver? resolver = null,
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? host = null)
    {
        // The host's providers, since a rule for a provider that is not configured grants nothing.
        using var scope = (host ?? _host!).Services.CreateScope();
        var services = scope.ServiceProvider;
        var session = services.GetRequiredService<IDocumentSession>();
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Headers["User-Agent"] = "role-map-tests";
        context.RequestServices = resolver is null ? services : new WithResolver(services, resolver);

        var (tokens, _) = await SocialSignIn.IssueForIdentityAsync(
            session,
            services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
            services.GetRequiredService<barakoCMS.Core.Interfaces.IDeviceGate>(),
            services.GetRequiredService<barakoCMS.Infrastructure.Auth.ITokenIssuer>(),
            services.GetRequiredService<barakoCMS.Infrastructure.Auth.Mfa.IMfaService>(),
            context,
            new OidcIdentity(issuer, "sub-" + Guid.NewGuid().ToString("N"), user.Email, true, null, null)
            {
                Claims = claims,
            },
            provider,
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
                    new ExternalRoleRule { Provider = "stub", Value = "staff", RoleId = SystemRoles.SuperAdminRoleId },
                    new ExternalRoleRule { Provider = "stub", Value = "staff", RoleId = elsewhere },
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
        var client = _host!.CreateClient();
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
            new { claim = "groups", rules = new[] { new { provider = "alpha", value = "staff", roleId = role.Id } } }, Ct);
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
                new { claim = "groups", rules = new[] { new { provider = "alpha", value = "staff", roleId } } }, Ct);
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
                Rules = [new ExternalRoleRule { Provider = "stub", Value = "staff", RoleId = unowned }],
            });
        var user = await PersonAsync();

        await SignInAsync(user, slug, Groups("staff"));

        (await MembershipsAsync(user.Id, slug)).Should().BeEmpty();
        var put = await ClientFor(slug, "Admin").PutAsJsonAsync("/api/tenants/external-role-map",
            new { claim = "groups", rules = new[] { new { provider = "alpha", value = "staff", roleId = unowned } } }, Ct);
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
            Rules = [new ExternalRoleRule { Provider = "stub", Value = "staff", RoleId = powerful.Id }],
        });
        var user = await PersonAsync();

        await SignInAsync(user, slug, Groups("staff"));

        (await MembershipsAsync(user.Id, slug)).Should().BeEmpty();
        (await ClientFor(slug, "Admin").PutAsJsonAsync("/api/tenants/external-role-map",
            new { claim = "groups", rules = new[] { new { provider = "alpha", value = "staff", roleId = powerful.Id } } }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- the link endpoints ----

    private async Task<(string SlugA, string SlugB, User Member, ExternalIdentity Link)> MemberInTwoTenantsAsync()
    {
        var slugA = await AdminTenantAsync();
        var slugB = await AdminTenantAsync();
        var member = await PersonAsync();
        var link = new ExternalIdentity
        {
            Id = ExternalIdentity.KeyOf("https://idp.test.example", "s-" + member.Id.ToString("N")),
            Issuer = "https://idp.test.example", Subject = "s-" + member.Id.ToString("N"),
            UserId = member.Id, Provider = "stub",
        };
        await StoreAsync(
            link,
            new Membership { Id = Guid.NewGuid(), UserId = member.Id, TenantSlug = slugA, Status = MembershipStatus.Active, RoleIds = [SystemRoles.UserRoleId] },
            new Membership { Id = Guid.NewGuid(), UserId = member.Id, TenantSlug = slugB, Status = MembershipStatus.Active, RoleIds = [SystemRoles.UserRoleId] });
        return (slugA, slugB, member, link);
    }

    private async Task<HttpClient> PlatformAdminClientAsync(string slug)
    {
        var client = _host!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin"));
        client.DefaultRequestHeaders.Add("X-Tenant", slug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"198.51.100.{Interlocked.Increment(ref _ipCounter) % 250 + 1}");
        return client;
    }

    private async Task<ExternalIdentity?> LinkAsync(string id)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<ExternalIdentity>(id, Ct);
    }

    [Fact]
    public async Task A_platform_administrator_lists_and_removes_the_links_of_a_user_who_is_in_two_tenants()
    {
        var (slugA, slugB, member, link) = await MemberInTwoTenantsAsync();
        var client = await PlatformAdminClientAsync(slugA);
        var route = $"/api/users/{member.Id}/external-identities";

        var list = await client.GetAsync(route, Ct);
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await list.Content.ReadAsStringAsync(Ct);
        body.Should().Contain(link.Id);
        body.Should().NotContain(link.Subject, "the provider's subject is not shown");

        (await client.DeleteAsync($"{route}/{link.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await LinkAsync(link.Id)).Should().BeNull("the link was global, so it is gone for both tenants");
        (await MembershipsAsync(member.Id, slugA)).Should().HaveCount(1);
        (await MembershipsAsync(member.Id, slugB)).Should().HaveCount(1, "removing a link takes nobody out of a tenant");
    }

    [Fact]
    public async Task A_tenant_administrator_cannot_list_or_remove_a_users_links_and_an_anonymous_caller_is_refused()
    {
        var (slug, _, member, link) = await MemberInTwoTenantsAsync();
        var route = $"/api/users/{member.Id}/external-identities";
        var admin = ClientFor(slug, "Admin");

        (await admin.GetAsync(route, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.DeleteAsync($"{route}/{link.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ClientFor(slug, null).GetAsync(route, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ClientFor(slug, null).DeleteAsync($"{route}/{link.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await LinkAsync(link.Id)).Should().NotBeNull("the refused delete left the link alone");
    }

    [Fact]
    public async Task A_link_is_removed_only_through_the_user_it_belongs_to_and_an_unknown_user_is_not_found()
    {
        var (slug, _, _, link) = await MemberInTwoTenantsAsync();
        var bystander = await PersonAsync();
        var client = await PlatformAdminClientAsync(slug);

        (await client.DeleteAsync($"/api/users/{bystander.Id}/external-identities/{link.Id}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/users/{Guid.NewGuid()}/external-identities", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await LinkAsync(link.Id)).Should().NotBeNull();
    }

    // ---- the provider a rule names, and what a rule may read ----

    [Fact]
    public async Task A_group_from_a_provider_other_than_the_one_the_rule_names_gives_nothing()
    {
        var (slug, teacher) = await TenantWithMapAsync();
        var user = await PersonAsync();

        await SignInAsync(user, slug, Groups("staff"), provider: "another");
        (await MembershipsAsync(user.Id, slug)).Should().BeEmpty("the rule reads stub, and this sign-in came through another");

        await SignInAsync(user, slug, Groups("staff"), provider: "stub");
        (await MembershipsAsync(user.Id, slug)).Single().RoleIds.Should().Equal(teacher.Id);
    }

    private static string DirectoryIssuer(string directory) =>
        OidcStubProvider.SharedIssuerTemplate.Replace("{tenantid}", directory);

    [Fact]
    public async Task A_rule_for_a_provider_with_a_templated_issuer_must_name_the_exact_issuer()
    {
        var slug = await AdminTenantAsync();
        var role = new Role { Id = Guid.NewGuid(), Name = Unique("Teacher"), TenantSlug = slug, Permissions = [], SystemCapabilities = [] };
        await StoreAsync(role);
        var client = ClientFor(slug, "Admin");
        var directory = Guid.NewGuid().ToString();

        foreach (var issuer in new[] { null, "", " ", OidcStubProvider.SharedIssuerTemplate, "not a url" })
        {
            (await client.PutAsJsonAsync("/api/tenants/external-role-map",
                new { claim = "groups", rules = new[] { new { provider = "entra", value = "staff", roleId = role.Id, issuer } } }, Ct))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest, $"'{issuer}' does not name one directory");
        }

        var ok = await client.PutAsJsonAsync("/api/tenants/external-role-map",
            new { claim = "groups", rules = new[] { new { provider = "entra", value = "staff", roleId = role.Id, issuer = DirectoryIssuer(directory) } } }, Ct);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ok.Content.ReadFromJsonAsync<RoleMapResponse>(Ct);
        body!.Rules.Should().HaveCount(1);
        body.Rules[0].Issuer.Should().Be(DirectoryIssuer(directory));

        (await client.PutAsJsonAsync("/api/tenants/external-role-map",
            new { claim = "groups", rules = new[] { new { provider = "beta", value = "staff", roleId = role.Id } } }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK, "a provider with a fixed issuer needs none");
    }

    [Fact]
    public async Task A_rule_with_an_issuer_gives_the_role_only_to_a_token_from_that_issuer()
    {
        var (slug, teacher) = await TenantWithMapAsync();
        var mine = DirectoryIssuer(Guid.NewGuid().ToString());
        var other = DirectoryIssuer(Guid.NewGuid().ToString());
        await StoreAsync(new ExternalRoleMap
        {
            Id = slug, Claim = "groups",
            Rules = [new ExternalRoleRule { Provider = "entra", Value = "staff", RoleId = teacher.Id, Issuer = mine }],
        });
        var user = await PersonAsync();

        await SignInAsync(user, slug, Groups("staff"), provider: "entra", issuer: other);
        (await MembershipsAsync(user.Id, slug)).Should().BeEmpty("another directory's token carries the group name but not the issuer");

        await SignInAsync(user, slug, Groups("staff"), provider: "entra", issuer: mine);
        (await MembershipsAsync(user.Id, slug)).Single().RoleIds.Should().Equal(teacher.Id);
    }

    [Fact]
    public async Task A_rule_must_name_a_configured_provider()
    {
        var slug = await AdminTenantAsync();
        var role = new Role { Id = Guid.NewGuid(), Name = Unique("Teacher"), TenantSlug = slug, Permissions = [], SystemCapabilities = [] };
        await StoreAsync(role);
        var client = ClientFor(slug, "Admin");

        foreach (var provider in new[] { "gamma", "", "ALPHA", "alpha " })
        {
            (await client.PutAsJsonAsync("/api/tenants/external-role-map",
                new { claim = "groups", rules = new[] { new { provider, value = "staff", roleId = role.Id } } }, Ct))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest, $"'{provider}' is not a configured provider");
        }

        (await client.PutAsJsonAsync("/api/tenants/external-role-map",
            new { claim = "groups", rules = new[] { new { provider = "beta", value = "staff", roleId = role.Id } } }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK, "a configured provider is accepted");
    }

    [Theory]
    [InlineData("email")]
    [InlineData("Name")]
    [InlineData("preferred_username")]
    [InlineData("sub")]
    [InlineData("phone_number")]
    [InlineData("address")]
    [InlineData("website")]
    [InlineData("profile")]
    [InlineData("middle_name")]
    [InlineData("gender")]
    [InlineData("birthdate")]
    [InlineData("locale")]
    [InlineData("zoneinfo")]
    [InlineData("updated_at")]
    public async Task A_rule_cannot_read_a_claim_the_person_can_edit_about_themselves(string claim)
    {
        var slug = await AdminTenantAsync();
        var role = new Role { Id = Guid.NewGuid(), Name = Unique("Teacher"), TenantSlug = slug, Permissions = [], SystemCapabilities = [] };
        await StoreAsync(role);
        var client = ClientFor(slug, "Admin");

        (await client.PutAsJsonAsync("/api/tenants/external-role-map",
            new { claim, rules = new[] { new { provider = "alpha", value = "staff", roleId = role.Id } } }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PutAsJsonAsync("/api/tenants/external-role-map",
            new { claim = "groups", rules = new[] { new { provider = "alpha", value = "staff", roleId = role.Id } } }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_value_with_a_space_at_either_end_is_refused()
    {
        var slug = await AdminTenantAsync();
        var role = new Role { Id = Guid.NewGuid(), Name = Unique("Teacher"), TenantSlug = slug, Permissions = [], SystemCapabilities = [] };
        await StoreAsync(role);
        var client = ClientFor(slug, "Admin");

        foreach (var value in new[] { " staff", "staff ", "staff\t" })
        {
            (await client.PutAsJsonAsync("/api/tenants/external-role-map",
                new { claim = "groups", rules = new[] { new { provider = "alpha", value, roleId = role.Id } } }, Ct))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }

    [Fact]
    public async Task Setting_and_removing_the_map_each_leave_an_audit_entry_naming_the_tenant()
    {
        var slug = await AdminTenantAsync();
        var role = new Role { Id = Guid.NewGuid(), Name = Unique("Teacher"), TenantSlug = slug, Permissions = [], SystemCapabilities = [] };
        await StoreAsync(role);
        var client = ClientFor(slug, "Admin");

        (await client.PutAsJsonAsync("/api/tenants/external-role-map",
            new { claim = "groups", rules = new[] { new { provider = "alpha", value = "staff", roleId = role.Id } } }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.DeleteAsync("/api/tenants/external-role-map", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _fixture.Services.CreateScope();
        var actions = (await scope.ServiceProvider.GetRequiredService<IQuerySession>().Query<AuditEvent>()
            .Where(e => e.TenantSlug == slug).ToListAsync(Ct)).Select(e => e.Action).ToList();
        actions.Should().Contain("tenant.external_role_map.updated");
        actions.Should().Contain("tenant.external_role_map.removed");
    }

    [Fact]
    public async Task A_role_an_administrator_set_after_the_membership_was_read_is_kept_when_the_grant_commits()
    {
        var (slug, teacher) = await TenantWithMapAsync();
        var user = await PersonAsync();
        var admins = Guid.NewGuid();
        await StoreAsync(
            new Role { Id = admins, Name = Unique("Other"), TenantSlug = slug, Permissions = [], SystemCapabilities = [] },
            new Membership { Id = Guid.NewGuid(), UserId = user.Id, TenantSlug = slug, Status = MembershipStatus.Active, RoleIds = [] });

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var stale = await session.Query<Membership>().SingleAsync(m => m.UserId == user.Id && m.TenantSlug == slug, Ct);

        using (var concurrent = _fixture.Services.CreateScope())
        {
            var other = concurrent.ServiceProvider.GetRequiredService<IDocumentSession>();
            other.Patch<Membership>(stale.Id).Set(x => x.RoleIds, new List<Guid> { admins });
            await other.SaveChangesAsync(Ct);
        }

        stale.RoleIds.Should().BeEmpty("the copy in hand predates the administrator's change");
        var actor = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim("UserId", user.Id.ToString())], "test"));
        await barakoCMS.Features.Tenants.Members.Members.AppendRolesAsync(
            session, actor, "tenant.member.updated", stale, [teacher.Id], new Dictionary<string, object>(), Ct);
        await session.SaveChangesAsync(Ct);

        (await MembershipsAsync(user.Id, slug)).Single().RoleIds.Should().BeEquivalentTo([admins, teacher.Id]);
    }

    [Fact]
    public async Task A_stored_map_on_a_claim_the_person_can_edit_gives_nothing()
    {
        var (slug, teacher) = await TenantWithMapAsync();
        await StoreAsync(new ExternalRoleMap
        {
            Id = slug, Claim = "phone_number",
            Rules = [new ExternalRoleRule { Provider = "stub", Value = "staff", RoleId = teacher.Id }],
        });
        var user = await PersonAsync();
        var claims = new Dictionary<string, IReadOnlyList<string>> { ["phone_number"] = ["staff"] };

        await SignInAsync(user, slug, claims);
        (await MembershipsAsync(user.Id, slug)).Should().BeEmpty("a map on a claim the person edits is filtered at sign-in");

        await StoreAsync(new ExternalRoleMap
        {
            Id = slug, Claim = "groups",
            Rules = [new ExternalRoleRule { Provider = "stub", Value = "staff", RoleId = teacher.Id }],
        });
        await SignInAsync(user, slug, Groups("staff"));
        (await MembershipsAsync(user.Id, slug)).Single().RoleIds.Should().Equal(teacher.Id);
    }

    [Fact]
    public async Task A_grant_does_not_reach_a_membership_suspended_after_it_was_read()
    {
        var (slug, teacher) = await TenantWithMapAsync();
        var user = await PersonAsync();
        await StoreAsync(new Membership
        {
            Id = Guid.NewGuid(), UserId = user.Id, TenantSlug = slug, Status = MembershipStatus.Active, RoleIds = [],
        });

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var stale = await session.Query<Membership>().SingleAsync(m => m.UserId == user.Id && m.TenantSlug == slug, Ct);

        using (var concurrent = _fixture.Services.CreateScope())
        {
            var other = concurrent.ServiceProvider.GetRequiredService<IDocumentSession>();
            other.Patch<Membership>(stale.Id).Set(x => x.Status, MembershipStatus.Suspended);
            await other.SaveChangesAsync(Ct);
        }

        stale.Status.Should().Be(MembershipStatus.Active, "the copy in hand predates the suspension");
        var actor = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim("UserId", user.Id.ToString())], "test"));
        await barakoCMS.Features.Tenants.Members.Members.AppendRolesAsync(
            session, actor, "tenant.member.updated", stale, [teacher.Id], new Dictionary<string, object>(), Ct);
        await session.SaveChangesAsync(Ct);

        var after = (await MembershipsAsync(user.Id, slug)).Single();
        after.Status.Should().Be(MembershipStatus.Suspended);
        after.RoleIds.Should().BeEmpty("a suspension is not undone by a grant that read the membership earlier");
    }

    [Fact]
    public async Task A_sign_in_that_grants_a_role_drops_the_cached_permissions_and_one_that_grants_nothing_does_not()
    {
        var (slug, _) = await TenantWithMapAsync();
        var user = await PersonAsync();
        var spy = new Moq.Mock<barakoCMS.Infrastructure.Services.IPermissionResolver>();

        await SignInAsync(user, slug, Groups("nothing-mapped"), resolver: spy.Object);
        spy.Verify(r => r.InvalidateUserPermissions(user.Id), Moq.Times.Never());

        await SignInAsync(user, slug, Groups("staff"), resolver: spy.Object);
        spy.Verify(r => r.InvalidateUserPermissions(user.Id), Moq.Times.Once());
    }

    [Fact]
    public async Task Removing_a_link_is_audited_with_the_provider_and_issuer()
    {
        var (slug, _, member, link) = await MemberInTwoTenantsAsync();
        var client = await PlatformAdminClientAsync(slug);

        (await client.DeleteAsync($"/api/users/{member.Id}/external-identities/{link.Id}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _fixture.Services.CreateScope();
        var entries = (await scope.ServiceProvider.GetRequiredService<IQuerySession>().Query<AuditEvent>()
            .Where(e => e.Action == "user.external_identity.removed" && e.TargetId == member.Id.ToString())
            .ToListAsync(Ct)).ToList();
        entries.Should().HaveCount(1);
        entries[0].Metadata!["provider"].ToString().Should().Be("stub");
        entries[0].Metadata!["issuer"].ToString().Should().Be(link.Issuer);
    }

    [Fact]
    public async Task A_user_manager_who_is_not_a_SuperAdmin_cannot_list_or_remove_a_SuperAdmins_links()
    {
        var slug = await AdminTenantAsync();
        var manager = new Role
        {
            Id = Guid.NewGuid(), Name = Unique("Manager"), Permissions = [],
            SystemCapabilities = [SystemCapabilities.ManageUsers],
        };
        var caller = await PersonAsync();
        var target = await PersonAsync();
        var plain = await PersonAsync();
        var link = new ExternalIdentity
        {
            Id = ExternalIdentity.KeyOf("https://idp.test.example", "s-" + target.Id.ToString("N")),
            Issuer = "https://idp.test.example", Subject = "s-" + target.Id.ToString("N"),
            UserId = target.Id, Provider = "stub",
        };
        var plainLink = new ExternalIdentity
        {
            Id = ExternalIdentity.KeyOf("https://idp.test.example", "s-" + plain.Id.ToString("N")),
            Issuer = "https://idp.test.example", Subject = "s-" + plain.Id.ToString("N"),
            UserId = plain.Id, Provider = "stub",
        };
        caller.RoleIds = [manager.Id];
        target.RoleIds = [SystemRoles.SuperAdminRoleId];
        await StoreAsync(manager, caller, target, link, plainLink);

        var client = _host!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _fixture.CreateToken(
                roles: [manager.Name],
                userId: caller.Id.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = slug, ["Username"] = caller.Username }));
        client.DefaultRequestHeaders.Add("X-Tenant", slug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"198.51.100.{Interlocked.Increment(ref _ipCounter) % 250 + 1}");

        (await client.GetAsync($"/api/users/{plain.Id}/external-identities", Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK, "the manager holds manage_users, so an ordinary user's links are theirs to list");
        (await client.GetAsync($"/api/users/{target.Id}/external-identities", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.DeleteAsync($"/api/users/{target.Id}/external-identities/{link.Id}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await LinkAsync(link.Id)).Should().NotBeNull("the refused delete left the link alone");

        (await (await PlatformAdminClientAsync(slug)).DeleteAsync($"/api/users/{target.Id}/external-identities/{link.Id}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent, "a SuperAdmin may");
    }

    // ---- a person with a second factor ----

    private async Task<byte[]> EnrollMfaAsync(User user)
    {
        var key = OtpNet.KeyGeneration.GenerateRandomKey(20);
        using var scope = _fixture.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<barakoCMS.Infrastructure.Auth.Mfa.IMfaSecretProtector>();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new MfaSecret
        {
            Id = user.Id, EncryptedSecret = protector.Protect(OtpNet.Base32Encoding.ToString(key)),
            Enabled = true, ConfirmedAt = DateTime.UtcNow,
        });
        await session.SaveChangesAsync(Ct);
        return key;
    }

    /// <summary>
    /// A valid code <paramref name="step"/> time steps ahead. Verify accepts one step either side and
    /// never a step it already took, so a second verify needs the next one.
    /// </summary>
    private static string Code(byte[] key, int step = 0) =>
        new OtpNet.Totp(key).ComputeTotp(DateTime.UtcNow.AddSeconds(30 * step));

    private async Task<HttpResponseMessage> VerifyAsync(
        string slug, string challenge, string code,
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? host = null)
    {
        var client = (host ?? _host!).CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant", slug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"198.51.100.{Interlocked.Increment(ref _ipCounter) % 250 + 1}");
        return await client.PostAsJsonAsync("/api/auth/mfa/verify", new { challengeToken = challenge, code }, Ct);
    }

    [Fact]
    public async Task A_person_with_a_second_factor_gets_the_mapped_role_only_after_it_passes()
    {
        var (slug, teacher) = await TenantWithMapAsync();
        var user = await PersonAsync();
        var key = await EnrollMfaAsync(user);

        var first = await SignInAsync(user, slug, Groups("staff"));

        first.RequiresMfa.Should().BeTrue();
        (await MembershipsAsync(user.Id, slug)).Should().BeEmpty("nothing is granted at first factor");

        var wrong = await VerifyAsync(slug, first.MfaChallenge!, "000000");
        wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await MembershipsAsync(user.Id, slug)).Should().BeEmpty("a wrong code grants nothing");

        var ok = await VerifyAsync(slug, first.MfaChallenge!, new OtpNet.Totp(key).ComputeTotp());
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync(Ct));
        var memberships = await MembershipsAsync(user.Id, slug);
        memberships.Should().HaveCount(1);
        memberships[0].RoleIds.Should().Equal(teacher.Id);

        var replay = await VerifyAsync(slug, first.MfaChallenge!, new OtpNet.Totp(key).ComputeTotp());
        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the code was spent");
        (await MembershipsAsync(user.Id, slug)).Single().RoleIds.Should().Equal(teacher.Id);
    }

    [Fact]
    public async Task A_tampered_challenge_and_a_challenge_with_nothing_mapped_grant_nothing()
    {
        var (slug, _) = await TenantWithMapAsync();
        var user = await PersonAsync();
        var key = await EnrollMfaAsync(user);

        var first = await SignInAsync(user, slug, Groups("staff"));
        var parts = first.MfaChallenge!.Split('.');
        var tampered = $"{parts[0]}.{parts[1]}.{(parts[2][0] == 'A' ? 'B' : 'A')}{parts[2][1..]}";
        (await VerifyAsync(slug, tampered, new OtpNet.Totp(key).ComputeTotp())).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await MembershipsAsync(user.Id, slug)).Should().BeEmpty();

        var other = await SignInAsync(user, slug, Groups("nothing-mapped"));
        other.RequiresMfa.Should().BeTrue();
        var verified = await VerifyAsync(slug, other.MfaChallenge!, Code(key, 1));
        verified.StatusCode.Should().Be(HttpStatusCode.OK, await verified.Content.ReadAsStringAsync(Ct));
        (await MembershipsAsync(user.Id, slug)).Should().BeEmpty("a challenge that carried no roles grants none");
    }

    [Fact]
    public async Task A_role_that_now_carries_a_platform_capability_is_not_given_when_the_second_factor_passes()
    {
        var (slug, teacher) = await TenantWithMapAsync();
        var user = await PersonAsync();
        var key = await EnrollMfaAsync(user);

        var first = await SignInAsync(user, slug, Groups("staff"));
        teacher.SystemCapabilities = [SystemCapabilities.ManageUsers];
        await StoreAsync(teacher);

        await VerifyAsync(slug, first.MfaChallenge!, new OtpNet.Totp(key).ComputeTotp());

        (await MembershipsAsync(user.Id, slug)).Should().BeEmpty("the role now carries a platform capability");
    }

    [Fact]
    public async Task A_role_deleted_before_the_second_factor_passed_is_not_given()
    {
        var (slug, teacher) = await TenantWithMapAsync();
        var user = await PersonAsync();
        var key = await EnrollMfaAsync(user);

        var first = await SignInAsync(user, slug, Groups("staff"));
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Delete<Role>(teacher.Id);
            await session.SaveChangesAsync(Ct);
        }

        await VerifyAsync(slug, first.MfaChallenge!, new OtpNet.Totp(key).ComputeTotp());

        (await MembershipsAsync(user.Id, slug)).Should().BeEmpty("the role no longer exists");
    }

    // ---- what a rule with no issuer, a spent challenge and a refused sign-in give ----

    [Fact]
    public async Task A_stored_rule_with_no_issuer_gives_nothing_through_a_provider_with_a_templated_issuer()
    {
        var (slug, teacher) = await TenantWithMapAsync();
        var directory = DirectoryIssuer(Guid.NewGuid().ToString());
        await StoreAsync(new ExternalRoleMap
        {
            Id = slug, Claim = "groups",
            Rules = [new ExternalRoleRule { Provider = "entra", Value = "staff", RoleId = teacher.Id }],
        });
        var user = await PersonAsync();

        await SignInAsync(user, slug, Groups("staff"), provider: "entra", issuer: directory);
        (await MembershipsAsync(user.Id, slug)).Should().BeEmpty("any directory can sign in through entra, so the rule must name one");

        await StoreAsync(new ExternalRoleMap
        {
            Id = slug, Claim = "groups",
            Rules = [new ExternalRoleRule { Provider = "entra", Value = "staff", RoleId = teacher.Id, Issuer = directory }],
        });
        await SignInAsync(user, slug, Groups("staff"), provider: "entra", issuer: directory);
        (await MembershipsAsync(user.Id, slug)).Single().RoleIds.Should().Equal(teacher.Id);
    }

    [Fact]
    public async Task A_challenge_grants_once_even_when_verified_again_with_a_fresh_code()
    {
        var (slug, teacher) = await TenantWithMapAsync();
        var user = await PersonAsync();
        var key = await EnrollMfaAsync(user);
        var first = await SignInAsync(user, slug, Groups("staff"));

        var ok = await VerifyAsync(slug, first.MfaChallenge!, Code(key));
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync(Ct));
        var membership = (await MembershipsAsync(user.Id, slug)).Single();
        membership.RoleIds.Should().Equal(teacher.Id);

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Patch<Membership>(membership.Id).Set(x => x.RoleIds, new List<Guid>());
            await session.SaveChangesAsync(Ct);
        }

        var again = await VerifyAsync(slug, first.MfaChallenge!, Code(key, 1));
        again.StatusCode.Should().Be(HttpStatusCode.OK, "the code is new, so the sign-in itself still succeeds");
        (await MembershipsAsync(user.Id, slug)).Single().RoleIds
            .Should().BeEmpty("the administrator took the role away and the spent challenge does not give it back");
    }

    private sealed class RefusingIssuer : barakoCMS.Infrastructure.Auth.ITokenIssuer
    {
        public Task<barakoCMS.Infrastructure.Auth.TokenIssueResult> IssueAccessTokenAsync(
            User user, string tenantSlug, IEnumerable<System.Security.Claims.Claim>? extraClaims = null,
            CancellationToken ct = default) =>
            Task.FromResult(barakoCMS.Infrastructure.Auth.TokenIssueResult.Denied("refused by the test"));
    }

    [Fact]
    public async Task A_sign_in_the_issuer_refuses_at_the_second_factor_grants_nothing()
    {
        var (slug, teacher) = await TenantWithMapAsync();
        var user = await PersonAsync();
        var key = await EnrollMfaAsync(user);
        var first = await SignInAsync(user, slug, Groups("staff"));
        using var resolver = FastEndpointsResolver.Keep();
        using var refusing = _host!.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddScoped<barakoCMS.Infrastructure.Auth.ITokenIssuer, RefusingIssuer>()));

        var refused = await VerifyAsync(slug, first.MfaChallenge!, Code(key), refusing);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await MembershipsAsync(user.Id, slug)).Should().BeEmpty("no token was issued, so no role was given");

        var ok = await VerifyAsync(slug, first.MfaChallenge!, Code(key, 1));
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync(Ct));
        (await MembershipsAsync(user.Id, slug)).Single().RoleIds
            .Should().Equal([teacher.Id], "the refused attempt did not spend the challenge");
    }

    [Fact]
    public async Task A_sign_in_the_issuer_refuses_without_a_second_factor_grants_nothing()
    {
        var (slug, teacher) = await TenantWithMapAsync();
        var user = await PersonAsync();
        using var resolver = FastEndpointsResolver.Keep();
        using var refusing = _host!.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddScoped<barakoCMS.Infrastructure.Auth.ITokenIssuer, RefusingIssuer>()));

        var refused = await SignInAsync(user, slug, Groups("staff"), host: refusing);
        refused.Allowed.Should().BeFalse();
        (await MembershipsAsync(user.Id, slug)).Should().BeEmpty("no token was issued, so no role was given");

        var ok = await SignInAsync(user, slug, Groups("staff"));
        ok.Allowed.Should().BeTrue();
        (await MembershipsAsync(user.Id, slug)).Single().RoleIds.Should().Equal(teacher.Id);
    }

    [Fact]
    public async Task Verify_drops_the_cached_permissions_only_when_it_grants_a_role()
    {
        var (slug, _) = await TenantWithMapAsync();
        var user = await PersonAsync();
        var key = await EnrollMfaAsync(user);
        var spy = new Moq.Mock<barakoCMS.Infrastructure.Services.IPermissionResolver>();
        using var resolver = FastEndpointsResolver.Keep();
        using var spied = _host!.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddScoped(_ => spy.Object)));

        var nothing = await SignInAsync(user, slug, Groups("nothing-mapped"));
        (await VerifyAsync(slug, nothing.MfaChallenge!, Code(key), spied)).StatusCode.Should().Be(HttpStatusCode.OK);
        spy.Verify(r => r.InvalidateUserPermissions(user.Id), Moq.Times.Never());

        var staff = await SignInAsync(user, slug, Groups("staff"));
        (await VerifyAsync(slug, staff.MfaChallenge!, Code(key, 1), spied)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await MembershipsAsync(user.Id, slug)).Should().HaveCount(1);
        spy.Verify(r => r.InvalidateUserPermissions(user.Id), Moq.Times.Once());
    }

    [Fact]
    public async Task One_persons_challenge_with_another_persons_code_grants_nothing_to_either()
    {
        var (slug, teacher) = await TenantWithMapAsync();
        var alice = await PersonAsync();
        var bob = await PersonAsync();
        var aliceKey = await EnrollMfaAsync(alice);
        var bobKey = await EnrollMfaAsync(bob);
        var aliceFirst = await SignInAsync(alice, slug, Groups("staff"));

        var crossed = await VerifyAsync(slug, aliceFirst.MfaChallenge!, Code(bobKey));
        crossed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await MembershipsAsync(alice.Id, slug)).Should().BeEmpty();
        (await MembershipsAsync(bob.Id, slug)).Should().BeEmpty();

        var ok = await VerifyAsync(slug, aliceFirst.MfaChallenge!, Code(aliceKey));
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync(Ct));
        (await MembershipsAsync(alice.Id, slug)).Single().RoleIds.Should().Equal(teacher.Id);
        (await MembershipsAsync(bob.Id, slug)).Should().BeEmpty("the challenge names Alice, so only Alice is granted");
    }

    [Fact]
    public async Task A_soft_deleted_role_is_not_given()
    {
        var (slug, teacher) = await TenantWithMapAsync();
        var user = await PersonAsync();
        teacher.DeletedAt = DateTime.UtcNow;
        await StoreAsync(teacher);

        await SignInAsync(user, slug, Groups("staff"));
        (await MembershipsAsync(user.Id, slug)).Should().BeEmpty("the role was deleted");

        teacher.DeletedAt = null;
        await StoreAsync(teacher);
        await SignInAsync(user, slug, Groups("staff"));
        (await MembershipsAsync(user.Id, slug)).Single().RoleIds.Should().Equal(teacher.Id);
    }

    [Fact]
    public void A_grant_whose_tenant_and_source_hold_a_bar_survives_the_challenge_round_trip()
    {
        var config = _host!.Services.GetRequiredService<IConfiguration>();
        var roles = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var userId = Guid.NewGuid();

        var (token, _) = barakoCMS.Infrastructure.Auth.Mfa.MfaChallengeToken.Create(config, userId,
            new barakoCMS.Infrastructure.Auth.Mfa.PendingExternalGrant("a|b", "stub|x", roles));
        var read = barakoCMS.Infrastructure.Auth.Mfa.MfaChallengeToken.Validated(config, token);

        read.Should().NotBeNull();
        read!.Value.UserId.Should().Be(userId);
        read.Value.Grant!.TenantSlug.Should().Be("a|b");
        read.Value.Grant.Source.Should().Be("stub|x");
        read.Value.Grant.RoleIds.Should().Equal(roles);
        read.Value.Grant.Id.Should().NotBeEmpty("each challenge's grant has its own id, which verify spends");
    }

    [Fact]
    public async Task A_grant_that_waited_on_a_suspension_reports_and_audits_nothing()
    {
        var (slug, teacher) = await TenantWithMapAsync();
        var user = await PersonAsync();
        var membership = new Membership
        {
            Id = Guid.NewGuid(), UserId = user.Id, TenantSlug = slug, Status = MembershipStatus.Active, RoleIds = [],
        };
        await StoreAsync(membership);

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var connection = store.Storage.Database.CreateConnection();
        await connection.OpenAsync(Ct);
        await using var hold = await connection.BeginTransactionAsync(Ct);
        await using (var holder = store.LightweightSession(Marten.Services.SessionOptions.ForTransaction(hold)))
        {
            var table = $"{store.Options.DatabaseSchemaName}.mt_doc_memberships";
            await holder.QueryAsync<int>($"select 1 from {table} where id = ? for update", Ct, membership.Id);
            holder.Patch<Membership>(membership.Id).Set(x => x.Status, MembershipStatus.Suspended);
            await holder.SaveChangesAsync(Ct);
        }

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var grant = Task.Run(async () =>
        {
            var granted = await barakoCMS.Infrastructure.Auth.ExternalRoleGrant.StageAsync(
                session, user, slug, [teacher.Id], "stub", Ct);
            await session.SaveChangesAsync(Ct);
            return granted;
        }, Ct);

        await WaitUntilBlockedOrDoneAsync(grant);
        await hold.CommitAsync(Ct);

        (await grant).Should().BeFalse("the membership was suspended by the time the grant could write");
        var after = (await MembershipsAsync(user.Id, slug)).Single();
        after.Status.Should().Be(MembershipStatus.Suspended);
        after.RoleIds.Should().BeEmpty();
        using var read = _fixture.Services.CreateScope();
        (await read.ServiceProvider.GetRequiredService<IQuerySession>().Query<AuditEvent>()
            .Where(e => e.TenantSlug == slug && e.Action == "tenant.member.updated")
            .ToListAsync(Ct)).Should().BeEmpty("nothing was granted, so nothing is audited");
    }

    /// <summary>Returns once a database lock is being waited on, or the task has finished.</summary>
    private async Task WaitUntilBlockedOrDoneAsync(Task task)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            await using var probe = store.QuerySession();
            var waiting = await probe.QueryAsync<int>("select count(*)::int from pg_locks where not granted", Ct);
            if (waiting.Single() > 0)
            {
                return;
            }

            await Task.Delay(50, Ct);
        }
    }
}

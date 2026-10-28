using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.ExternalAuth;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Tenants;

/// <summary>
/// A tenant's sign-up policy decides who joins it through registration, social sign-in and
/// invitations, and a tenant with no policy, like the default tenant, behaves as before (#914).
/// </summary>
[Collection("Integration1")]
public class TenantSignUpPolicyTests
{
    private const string Password = "ValidPassword123!";
    private static int _ip;

    private readonly IntegrationTestFixture _fixture;

    public TenantSignUpPolicyTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NextIp() => $"2001:db8:914:{Interlocked.Increment(ref _ip):x}::1";

    private HttpClient Client(string? tenant)
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        if (tenant is not null)
        {
            client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        }
        return client;
    }

    private async Task<Role> MemberRoleAsync(string? tenant = null)
    {
        var role = new Role { Id = Guid.NewGuid(), Name = $"member-{Guid.NewGuid():N}"[..20], Description = "sign-up role", TenantSlug = tenant };
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(role);
        await session.SaveChangesAsync(Ct);
        return role;
    }

    private async Task<string> TenantAsync(TenantSignUp signUp, Guid? roleId = null, TenantStatus status = TenantStatus.Active)
    {
        var slug = $"su-{Guid.NewGuid():N}"[..16];
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, SignUp = signUp, SignUpRoleId = roleId, Status = status });
        await session.SaveChangesAsync(Ct);
        _fixture.Services.GetRequiredService<barakoCMS.Infrastructure.Multitenancy.ITenantDomainSource>().Invalidate();
        return slug;
    }

    private static (string Username, string Email) Identity()
    {
        var id = $"su{Guid.NewGuid():N}"[..20];
        return (id, $"{id}@example.com");
    }

    private Task<HttpResponseMessage> RegisterAsync(string? tenant, string username, string email) =>
        Client(tenant).PostAsJsonAsync("/api/auth/register", new { username, email, password = Password }, Ct);

    private async Task<PendingRegistration?> PendingAsync(string email)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<PendingRegistration>().FirstOrDefaultAsync(p => p.Email == email && !p.Consumed, Ct);
    }

    /// <summary>A pending registration as the register endpoint stores it, with a token this test knows.</summary>
    private async Task<string> SeedPendingAsync(string username, string email, string? tenantSlug)
    {
        var pending = new PendingRegistration
        {
            Username = username,
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            TenantSlug = tenantSlug,
        };
        var (token, hash) = EmailVerificationToken.Create(pending.Id);
        pending.TokenHash = hash;

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(pending);
        await session.SaveChangesAsync(Ct);
        return token;
    }

    private async Task<User?> UserAsync(string email)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<User>().FirstOrDefaultAsync(u => u.NormalizedEmail == email, Ct);
    }

    private async Task<IReadOnlyList<Membership>> MembershipsAsync(Guid userId, string slug)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<Membership>().Where(m => m.UserId == userId && m.TenantSlug == slug).ToListAsync(Ct);
    }

    private Task<HttpResponseMessage> LoginAsync(string tenant, string username) =>
        Client(tenant).PostAsJsonAsync("/api/auth/login", new { username, password = Password }, Ct);

    [Fact]
    public async Task A_person_who_registers_on_an_open_tenant_becomes_a_member_with_its_role_and_can_sign_in()
    {
        var role = await MemberRoleAsync();
        var slug = await TenantAsync(TenantSignUp.Open, role.Id);
        var (username, email) = Identity();

        var registered = await RegisterAsync(slug, username, email);
        registered.StatusCode.Should().Be(HttpStatusCode.OK, await registered.Content.ReadAsStringAsync(Ct));
        (await PendingAsync(email))!.TenantSlug.Should().Be(slug, "verification has to know which tenant the registration joins");

        var token = await SeedPendingAsync(username, email, slug);
        var verified = await Client(null).PostAsJsonAsync("/api/auth/register/verify", new { token, password = Password }, Ct);
        verified.StatusCode.Should().Be(HttpStatusCode.OK, await verified.Content.ReadAsStringAsync(Ct));

        var user = await UserAsync(email);
        user.Should().NotBeNull();
        user!.RoleIds.Should().BeEmpty("the tenant's role is what an open sign-up holds, not a global one");

        var memberships = await MembershipsAsync(user.Id, slug);
        memberships.Should().HaveCount(1);
        memberships[0].Status.Should().Be(MembershipStatus.Active);
        memberships[0].RoleIds.Should().Equal(role.Id);

        var login = await LoginAsync(slug, username);
        login.StatusCode.Should().Be(HttpStatusCode.OK, "the membership is what the issuer looks for: " + await login.Content.ReadAsStringAsync(Ct));
        var body = await login.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("token").GetString().Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData(TenantSignUp.InviteOnly)]
    [InlineData(TenantSignUp.Closed)]
    public async Task Registration_is_refused_by_a_tenant_that_takes_no_sign_ups(TenantSignUp policy)
    {
        var slug = await TenantAsync(policy);
        var (username, email) = Identity();

        var refused = await RegisterAsync(slug, username, email);

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PendingAsync(email)).Should().BeNull("nothing is recorded for a sign-up the tenant does not take");
    }

    [Fact]
    public async Task Registration_is_refused_by_a_suspended_open_tenant()
    {
        var role = await MemberRoleAsync();
        var slug = await TenantAsync(TenantSignUp.Open, role.Id, TenantStatus.Suspended);
        var (username, email) = Identity();

        (await RegisterAsync(slug, username, email)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_verification_after_the_tenant_stopped_taking_sign_ups_creates_nothing()
    {
        var slug = await TenantAsync(TenantSignUp.InviteOnly);
        var (username, email) = Identity();
        var token = await SeedPendingAsync(username, email, slug);

        var verified = await Client(null).PostAsJsonAsync("/api/auth/register/verify", new { token, password = Password }, Ct);

        verified.StatusCode.Should().NotBe(HttpStatusCode.OK);
        (await UserAsync(email)).Should().BeNull();
    }

    [Fact]
    public async Task The_default_tenant_and_a_tenant_with_no_policy_register_as_before()
    {
        var notSet = await TenantAsync(TenantSignUp.NotSet);

        foreach (var tenant in new string?[] { null, notSet })
        {
            var (username, email) = Identity();
            var registered = await RegisterAsync(tenant, username, email);
            registered.StatusCode.Should().Be(HttpStatusCode.OK, await registered.Content.ReadAsStringAsync(Ct));

            var pending = await PendingAsync(email);
            pending.Should().NotBeNull();
            pending!.TenantSlug.Should().BeNull("no policy joins the registration to a tenant");

            var token = await SeedPendingAsync(username, email, null);
            (await Client(null).PostAsJsonAsync("/api/auth/register/verify", new { token, password = Password }, Ct))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            var user = await UserAsync(email);
            user!.RoleIds.Should().Equal([barakoCMS.Data.DataSeeder.UserRoleId], "the global User role, as registration always gave");
        }
    }

    private async Task<SocialSignIn.Tokens> SocialAsync(string email, string club)
    {
        // Called outside a request, so token creation reads FastEndpoints' static resolver, which a
        // class that disposed its own host can leave pointing at a disposed provider.
        FastEndpointsResolver.RepairIfDisposed([_fixture.Services]);

        using var scope = _fixture.Services.CreateScope();
        var provider = scope.ServiceProvider;
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Headers["User-Agent"] = "sign-up-policy-tests";

        var (tokens, _) = await SocialSignIn.IssueForProviderAsync(
            provider.GetRequiredService<IDocumentSession>(),
            provider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
            provider.GetRequiredService<barakoCMS.Core.Interfaces.IDeviceGate>(),
            provider.GetRequiredService<ITokenIssuer>(),
            provider.GetRequiredService<barakoCMS.Infrastructure.Auth.Mfa.IMfaService>(),
            context, BarakoCMS.ExternalAuth.ExternalProviders.Google, "sub-" + Guid.NewGuid().ToString("N"),
            email, emailVerified: true, club, Ct);
        return tokens;
    }

    [Fact]
    public async Task Social_sign_in_on_an_open_tenant_creates_a_member_and_signs_them_in()
    {
        var role = await MemberRoleAsync();
        var slug = await TenantAsync(TenantSignUp.Open, role.Id);
        var email = $"social-{Guid.NewGuid():N}@example.com";

        var tokens = await SocialAsync(email, slug);

        tokens.Allowed.Should().BeTrue();
        var user = await UserAsync(email);
        var memberships = await MembershipsAsync(user!.Id, slug);
        memberships.Should().HaveCount(1);
        memberships[0].RoleIds.Should().Equal(role.Id);
    }

    [Fact]
    public async Task Social_sign_in_on_an_invite_only_tenant_creates_no_account()
    {
        var slug = await TenantAsync(TenantSignUp.InviteOnly);
        var email = $"social-{Guid.NewGuid():N}@example.com";

        var tokens = await SocialAsync(email, slug);

        tokens.Allowed.Should().BeFalse();
        (await UserAsync(email)).Should().BeNull("a refused sign-up leaves no account behind");
    }

    [Fact]
    public async Task Open_sign_up_does_not_bring_back_a_member_an_administrator_removed()
    {
        var role = await MemberRoleAsync();
        var slug = await TenantAsync(TenantSignUp.Open, role.Id);
        var email = $"social-{Guid.NewGuid():N}@example.com";
        (await SocialAsync(email, slug)).Allowed.Should().BeTrue("the control: the first sign-up joins");

        var user = await UserAsync(email);
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var membership = (await MembershipsAsync(user!.Id, slug)).Single();
            membership.Status = MembershipStatus.Removed;
            session.Store(membership);
            await session.SaveChangesAsync(Ct);
        }

        var again = await SocialAsync(email, slug);

        again.Allowed.Should().BeFalse("signing up again is not a way back in");
        var memberships = await MembershipsAsync(user!.Id, slug);
        memberships.Should().HaveCount(1);
        memberships[0].Status.Should().Be(MembershipStatus.Removed);
    }

    [Fact]
    public async Task Social_sign_in_does_not_make_an_existing_account_a_member_of_an_open_tenant()
    {
        var role = await MemberRoleAsync();
        var slug = await TenantAsync(TenantSignUp.Open, role.Id);
        var (username, email) = Identity();
        var existing = new User { Id = Guid.NewGuid(), Username = username, Email = email, PasswordHash = "" };
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(existing);
            await session.SaveChangesAsync(Ct);
        }

        var tokens = await SocialAsync(email, slug);

        tokens.Allowed.Should().BeFalse("an existing account signs in, and it is not a member here");
        (await MembershipsAsync(existing.Id, slug)).Should().BeEmpty();
    }

    private async Task<HttpResponseMessage> InviteAsync(string slug, string email)
    {
        var admin = await TenantProfileSeed.AdminInAsync(_fixture, slug, Ct);
        return await admin.PostAsJsonAsync("/api/tenants/members", new { email, roleIds = Array.Empty<Guid>() }, Ct);
    }

    [Fact]
    public async Task An_invitation_works_on_an_invite_only_tenant()
    {
        var slug = await TenantAsync(TenantSignUp.InviteOnly);

        var invited = await InviteAsync(slug, $"invitee-{Guid.NewGuid():N}@example.com");

        invited.StatusCode.Should().Be(HttpStatusCode.OK, await invited.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_closed_tenant_refuses_an_invitation_that_would_create_an_account_and_adds_an_existing_one()
    {
        var slug = await TenantAsync(TenantSignUp.Closed);
        var newcomer = $"invitee-{Guid.NewGuid():N}@example.com";

        var refused = await InviteAsync(slug, newcomer);
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await UserAsync(newcomer)).Should().BeNull();

        var existing = $"existing-{Guid.NewGuid():N}@example.com";
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User { Id = Guid.NewGuid(), Username = existing, Email = existing, PasswordHash = string.Empty });
            await session.SaveChangesAsync(Ct);
        }

        (await InviteAsync(slug, existing)).StatusCode.Should().Be(HttpStatusCode.OK, "an account that exists is not a sign-up");
    }

    private async Task<HttpResponseMessage> SetPolicyAsync(string slug, object body)
    {
        var admin = _fixture.CreateClient();
        admin.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin"));
        return await admin.PutAsJsonAsync($"/api/tenants/{slug}", body, Ct);
    }

    [Fact]
    public async Task The_policy_is_set_through_the_tenant_api_and_refuses_a_role_that_reaches_past_the_tenant()
    {
        var slug = await TenantAsync(TenantSignUp.NotSet);
        var role = await MemberRoleAsync(slug);

        var set = await SetPolicyAsync(slug, new { name = slug, signUp = "Open", signUpRoleId = role.Id });
        set.StatusCode.Should().Be(HttpStatusCode.OK, await set.Content.ReadAsStringAsync(Ct));
        var body = await set.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("signUp").GetString().Should().Be("Open");
        body.GetProperty("signUpRoleId").GetGuid().Should().Be(role.Id);

        (await SetPolicyAsync(slug, new { name = slug, signUp = "Open" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "Open needs a role");
        (await SetPolicyAsync(slug, new { name = slug, signUp = "Open", signUpRoleId = SystemRoles.SuperAdminRoleId }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "whoever signs up must not become a SuperAdmin");

        var platform = new Role
        {
            Id = Guid.NewGuid(), Name = $"plat-{Guid.NewGuid():N}"[..18],
            SystemCapabilities = [SystemCapabilities.ManageTenants],
        };
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(platform);
            await session.SaveChangesAsync(Ct);
        }

        (await SetPolicyAsync(slug, new { name = slug, signUp = "Open", signUpRoleId = platform.Id }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "nor holder of a platform capability");
        (await SetPolicyAsync(slug, new { name = slug, signUp = "Closed", signUpRoleId = role.Id }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "a role is only read with Open");

        var elsewhere = await MemberRoleAsync(await TenantAsync(TenantSignUp.NotSet));
        (await SetPolicyAsync(slug, new { name = slug, signUp = "Open", signUpRoleId = elsewhere.Id }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "another tenant's role is not this tenant's to give");

        var untouched = await SetPolicyAsync(slug, new { name = slug });
        (await untouched.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("signUp").GetString().Should().Be("Open",
            "an update that leaves signUp out keeps the policy");
    }
}

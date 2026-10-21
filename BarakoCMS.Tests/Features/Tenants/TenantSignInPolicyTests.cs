using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.ExternalAuth;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Tenants;

/// <summary>
/// A tenant can require a second factor, of everyone or of members holding chosen roles, limit how
/// a sign-in starts, and shorten its sessions. All of it is decided when a token for the tenant is
/// issued, on top of what the roles require (#920).
/// </summary>
[Collection("Sequential")]
public class TenantSignInPolicyTests
{
    private const string Password = "ValidPassword123!";
    private static int _ip;

    private readonly IntegrationTestFixture _fixture;

    public TenantSignInPolicyTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Client(string tenant)
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"2001:db8:920::{Interlocked.Increment(ref _ip):x}");
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        return client;
    }

    private async Task StoreAsync<T>(params T[] documents) where T : notnull
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(documents);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<Role> RoleAsync(string? tenant = null)
    {
        var role = new Role { Id = Guid.NewGuid(), Name = $"cashier-{Guid.NewGuid():N}"[..20], TenantSlug = tenant };
        await StoreAsync(role);
        return role;
    }

    private async Task<string> TenantAsync(TenantSignInPolicy signIn)
    {
        var slug = $"si-{Guid.NewGuid():N}"[..16];
        await StoreAsync(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, SignIn = signIn });
        return slug;
    }

    private async Task<User> MemberAsync(Guid roleId, params string[] tenants)
    {
        var id = Guid.NewGuid();
        var user = new User
        {
            Id = id,
            Username = $"si{id:N}"[..20],
            Email = $"si{id:N}@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
        };
        await StoreAsync(user);
        await StoreAsync(tenants.Select(slug => new Membership
        {
            Id = Guid.NewGuid(), UserId = id, TenantSlug = slug, Status = MembershipStatus.Active, RoleIds = [roleId],
        }).ToArray());
        return user;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> LoginAsync(string tenant, User user)
    {
        var response = await Client(tenant).PostAsJsonAsync("/api/auth/login", new { username = user.Username, password = Password }, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    [Fact]
    public async Task A_role_that_requires_MFA_in_one_tenant_asks_to_enrol_there_and_not_in_another()
    {
        var role = await RoleAsync();
        var strict = await TenantAsync(new TenantSignInPolicy { MfaRoleIds = [role.Id] });
        var relaxed = await TenantAsync(new TenantSignInPolicy());
        var user = await MemberAsync(role.Id, strict, relaxed);

        var there = await LoginAsync(strict, user);
        there.Status.Should().Be(HttpStatusCode.OK, there.Body.ToString());
        there.Body.GetProperty("mfaEnrollmentRequired").GetBoolean().Should().BeTrue("the tenant requires MFA of this role");

        var elsewhere = await LoginAsync(relaxed, user);
        elsewhere.Status.Should().Be(HttpStatusCode.OK, elsewhere.Body.ToString());
        elsewhere.Body.GetProperty("mfaEnrollmentRequired").GetBoolean().Should().BeFalse("the other tenant does not");
    }

    [Fact]
    public async Task A_tenant_that_requires_MFA_of_everyone_asks_any_member_to_enrol()
    {
        var role = await RoleAsync();
        var slug = await TenantAsync(new TenantSignInPolicy { RequireMfa = true });
        var user = await MemberAsync(role.Id, slug);

        var login = await LoginAsync(slug, user);

        login.Status.Should().Be(HttpStatusCode.OK, login.Body.ToString());
        login.Body.GetProperty("mfaEnrollmentRequired").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_tenant_session_length_shortens_the_refresh_lifetime_at_sign_in_and_at_refresh()
    {
        var role = await RoleAsync();
        var short_ = await TenantAsync(new TenantSignInPolicy { SessionMinutes = 60 });
        var plain = await TenantAsync(new TenantSignInPolicy());
        var user = await MemberAsync(role.Id, short_, plain);

        var control = await LoginAsync(plain, user);
        control.Body.GetProperty("refreshTokenExpiry").GetDateTime().Should()
            .BeCloseTo(DateTime.UtcNow.AddDays(7), TimeSpan.FromMinutes(5), "the deployment's seven days, where the tenant sets nothing");

        var login = await LoginAsync(short_, user);
        login.Status.Should().Be(HttpStatusCode.OK, login.Body.ToString());
        login.Body.GetProperty("refreshTokenExpiry").GetDateTime().Should()
            .BeCloseTo(DateTime.UtcNow.AddMinutes(60), TimeSpan.FromMinutes(5));

        var refreshed = await Client(short_).PostAsJsonAsync("/api/auth/refresh",
            new { refreshToken = login.Body.GetProperty("refreshToken").GetString() }, Ct);
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK, await refreshed.Content.ReadAsStringAsync(Ct));
        (await refreshed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("refreshTokenExpiry").GetDateTime().Should()
            .BeCloseTo(DateTime.UtcNow.AddMinutes(60), TimeSpan.FromMinutes(5), "a rotation is held to the same length");
    }

    [Fact]
    public async Task A_session_shorter_than_an_access_token_cuts_the_access_token_too()
    {
        var role = await RoleAsync();
        var slug = await TenantAsync(new TenantSignInPolicy { SessionMinutes = 5 });
        var user = await MemberAsync(role.Id, slug);

        var login = await LoginAsync(slug, user);

        login.Status.Should().Be(HttpStatusCode.OK, login.Body.ToString());
        login.Body.GetProperty("expiry").GetDateTime().Should().BeBefore(DateTime.UtcNow.AddMinutes(6));
    }

    [Fact]
    public async Task A_tenant_that_allows_only_codes_refuses_a_password_sign_in()
    {
        var role = await RoleAsync();
        var codesOnly = await TenantAsync(new TenantSignInPolicy { Methods = [TenantSignInPolicy.Otp] });
        var user = await MemberAsync(role.Id, codesOnly);

        var login = await LoginAsync(codesOnly, user);
        login.Status.Should().Be(HttpStatusCode.Forbidden);

        var code = await Client(codesOnly).PostAsJsonAsync("/api/auth/otp/request", new { email = user.Email }, Ct);
        code.StatusCode.Should().Be(HttpStatusCode.OK, "the method the tenant allows still starts");
    }

    [Fact]
    public async Task A_tenant_that_allows_only_passwords_refuses_codes_and_social_sign_in()
    {
        var role = await RoleAsync();
        var passwordOnly = await TenantAsync(new TenantSignInPolicy { Methods = [TenantSignInPolicy.Password] });
        var user = await MemberAsync(role.Id, passwordOnly);

        (await LoginAsync(passwordOnly, user)).Status.Should().Be(HttpStatusCode.OK, "the control: passwords are allowed");

        var code = await Client(passwordOnly).PostAsJsonAsync("/api/auth/otp/request", new { email = user.Email }, Ct);
        code.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var scope = _fixture.Services.CreateScope();
        var provider = scope.ServiceProvider;
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Headers["User-Agent"] = "sign-in-policy-tests";
        var social = await SocialSignIn.IssueAsync(
            provider.GetRequiredService<IDocumentSession>(),
            provider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
            provider.GetRequiredService<barakoCMS.Core.Interfaces.IDeviceGate>(),
            provider.GetRequiredService<ITokenIssuer>(),
            provider.GetRequiredService<barakoCMS.Infrastructure.Auth.Mfa.IMfaService>(),
            context, user.Email, emailVerified: true, passwordOnly, Ct);
        social.Allowed.Should().BeFalse("the tenant does not allow social sign-in");
    }

    private async Task<string> CodeForAsync(User user)
    {
        const string code = "246810";
        await StoreAsync(new OtpCode
        {
            Email = user.Email.ToLowerInvariant(),
            CodeHash = BCrypt.Net.BCrypt.HashPassword(code),
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
        });
        return code;
    }

    [Fact]
    public async Task A_code_requested_elsewhere_does_not_sign_in_to_a_tenant_that_refuses_codes()
    {
        var role = await RoleAsync();
        var passwordOnly = await TenantAsync(new TenantSignInPolicy { Methods = [TenantSignInPolicy.Password] });
        var codesAllowed = await TenantAsync(new TenantSignInPolicy());
        var user = await MemberAsync(role.Id, passwordOnly, codesAllowed);

        var control = await Client(codesAllowed).PostAsJsonAsync("/api/auth/otp/verify",
            new { email = user.Email, code = await CodeForAsync(user) }, Ct);
        control.StatusCode.Should().Be(HttpStatusCode.OK, "the control: " + await control.Content.ReadAsStringAsync(Ct));

        var refused = await Client(passwordOnly).PostAsJsonAsync("/api/auth/otp/verify",
            new { email = user.Email, code = await CodeForAsync(user) }, Ct);
        var body = await refused.Content.ReadAsStringAsync(Ct);
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
    }

    private async Task<HttpResponseMessage> SetAsync(string slug, object signIn)
    {
        var admin = _fixture.CreateClient();
        admin.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin"));
        return await admin.PutAsJsonAsync($"/api/tenants/{slug}", new { name = slug, signIn }, Ct);
    }

    [Fact]
    public async Task The_policy_is_set_through_the_tenant_api_and_bad_values_are_refused()
    {
        var slug = await TenantAsync(new TenantSignInPolicy());
        var role = await RoleAsync(slug);

        var set = await SetAsync(slug, new { requireMfa = false, mfaRoleIds = new[] { role.Id }, methods = new[] { "Password", "otp" }, sessionMinutes = 120 });
        set.StatusCode.Should().Be(HttpStatusCode.OK, await set.Content.ReadAsStringAsync(Ct));
        var signIn = (await set.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("signIn");
        signIn.GetProperty("mfaRoleIds").EnumerateArray().Select(e => e.GetGuid()).Should().Equal(role.Id);
        signIn.GetProperty("methods").EnumerateArray().Select(e => e.GetString()).Should().Equal("password", "otp");
        signIn.GetProperty("sessionMinutes").GetInt32().Should().Be(120);

        (await SetAsync(slug, new { methods = new[] { "carrier-pigeon" } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SetAsync(slug, new { sessionMinutes = 1 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SetAsync(slug, new { sessionMinutes = 60 * 24 * 30 })).StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a tenant can shorten the deployment's session, not lengthen it");
        (await SetAsync(slug, new { mfaRoleIds = new[] { Guid.NewGuid() } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed class SmsMethod : ISignInMethod
    {
        public string Name => "sms";
    }

    private async Task<HttpResponseMessage> SetOnAsync(WebApplicationFactory<Program> host, string slug, object signIn)
    {
        var admin = host.CreateClient();
        admin.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin"));
        return await admin.PutAsJsonAsync($"/api/tenants/{slug}", new { name = slug, signIn }, Ct);
    }

    [Fact]
    public async Task A_module_method_registered_through_ISignInMethod_is_accepted_on_a_tenant_write()
    {
        var slug = await TenantAsync(new TenantSignInPolicy());
        using var host = _fixture.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<ISignInMethod>(new SmsMethod())));

        var set = await SetOnAsync(host, slug, new { methods = new[] { "sms" } });

        set.StatusCode.Should().Be(HttpStatusCode.OK, await set.Content.ReadAsStringAsync(Ct));
        var signIn = (await set.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("signIn");
        signIn.GetProperty("methods").EnumerateArray().Select(e => e.GetString()).Should().Equal("sms");
    }

    [Fact]
    public async Task An_unregistered_method_is_still_refused_on_a_tenant_write()
    {
        var slug = await TenantAsync(new TenantSignInPolicy());

        var set = await SetAsync(slug, new { methods = new[] { "sms" } });

        set.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await set.Content.ReadAsStringAsync(Ct)).Should().Contain("A sign-in method is one of: password, otp, social");
    }

    [Fact]
    public void AllowsExplicitly_is_false_for_an_empty_list_and_true_when_named()
    {
        new TenantSignInPolicy { Methods = [] }.AllowsExplicitly("sms").Should().BeFalse();
        new TenantSignInPolicy { Methods = ["password"] }.AllowsExplicitly("sms").Should().BeFalse();
        new TenantSignInPolicy { Methods = ["sms"] }.AllowsExplicitly("sms").Should().BeTrue();
        new TenantSignInPolicy { Methods = ["SMS"] }.AllowsExplicitly("sms").Should().BeTrue();
    }

    [Fact]
    public async Task The_default_tenant_does_not_allow_an_unnamed_module_method()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();

        (await TenantSignIn.AllowsExplicitlyAsync(session, null, "sms", Ct)).Should().BeFalse();
        (await TenantSignIn.AllowsExplicitlyAsync(session, Tenant.DefaultSlug, "sms", Ct)).Should().BeFalse();
        (await TenantSignIn.AllowsExplicitlyAsync(session, "no-such-tenant", "sms", Ct)).Should().BeFalse();

        var empty = await TenantAsync(new TenantSignInPolicy());
        var named = await TenantAsync(new TenantSignInPolicy { Methods = ["sms"] });
        (await TenantSignIn.AllowsExplicitlyAsync(session, empty, "sms", Ct)).Should().BeFalse();
        (await TenantSignIn.AllowsExplicitlyAsync(session, named, "sms", Ct)).Should().BeTrue();
    }
}

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using Xunit;
using LoginResponse = barakoCMS.Features.Auth.Login.Response;
using MfaModels = barakoCMS.Features.Auth.Mfa;

namespace BarakoCMS.Tests.Mfa;

[Collection("Sequential")]
public class AdminMfaResetTests
{
    private const string Password = "P@ssword123!Ab";
    private static int _ipCounter;

    private readonly IntegrationTestFixture _factory;
    private readonly string _ip;

    public AdminMfaResetTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        var n = Interlocked.Increment(ref _ipCounter);
        _ip = $"198.51.100.{(n + 60) % 250 + 1}";
    }

    private HttpClient Client(string? bearer = null, string? tenant = null)
    {
        var c = _factory
            .WithWebHostBuilder(b => b.ConfigureServices(s =>
                s.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, TestRemoteIpFilter>()))
            .CreateClient();
        c.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, _ip);
        if (tenant is not null) c.DefaultRequestHeaders.Add("X-Tenant", tenant);
        if (bearer is not null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return c;
    }

    private async Task<HttpClient> AdminAsync(string? tenant = null) =>
        Client(await _factory.StoredUserTokenAsync("SuperAdmin"), tenant);

    private async Task<Guid> StoreRoleAsync(bool requireMfa)
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        s.Store(new Role
        {
            Id = id,
            Name = $"mfa-reset-{Guid.NewGuid():N}"[..24],
            SystemCapabilities = ["manage_roles"],
            RequireMfa = requireMfa,
        });
        await s.SaveChangesAsync();
        return id;
    }

    private async Task<(Guid Id, string Username)> SeedUserAsync(bool requireMfa = false)
    {
        var roleId = await StoreRoleAsync(requireMfa);
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var username = $"mfar-{Guid.NewGuid():N}";
        var id = Guid.NewGuid();
        s.Store(new User
        {
            Id = id,
            Username = username,
            Email = $"{username}@test.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            RoleIds = [roleId],
        });
        await s.SaveChangesAsync();
        return (id, username);
    }

    private async Task<LoginResponse> LoginAsync(string username)
    {
        var res = await Client().PostAsJsonAsync("/api/auth/login",
            new barakoCMS.Features.Auth.Login.Request { Username = username, Password = Password });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    private async Task<List<string>> EnrollAsync(string username)
    {
        var client = Client((await LoginAsync(username)).Token);
        var setup = (await (await client.PostAsync("/api/auth/mfa/setup", null))
            .Content.ReadFromJsonAsync<MfaModels.SetupResponse>())!;
        var code = new Totp(Base32Encoding.ToBytes(setup.Secret)).ComputeTotp();
        var enable = await client.PostAsJsonAsync("/api/auth/mfa/enable", new MfaModels.CodeRequest { Code = code });
        enable.StatusCode.Should().Be(HttpStatusCode.OK, await enable.Content.ReadAsStringAsync());
        var recovery = (await enable.Content.ReadFromJsonAsync<MfaModels.EnableResponse>())!.RecoveryCodes;
        recovery.Should().HaveCount(10);
        return recovery;
    }

    private async Task<MfaSecret?> StoredSecretAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<MfaSecret>(userId);
    }

    private static Task<HttpResponseMessage> ResetAsync(HttpClient client, Guid userId, string? reason = "lost the phone") =>
        client.PostAsJsonAsync($"/api/users/{userId}/mfa/reset", new { Reason = reason });

    private async Task<bool> StatusEnabledAsync(Guid userId, string username)
    {
        var token = (await LoginAsync(username)).Token;
        var status = await Client(token).GetFromJsonAsync<MfaModels.StatusResponse>("/api/auth/mfa/status");
        return status!.Enabled;
    }

    [Fact]
    public async Task An_administrator_resets_a_users_MFA_and_gets_200()
    {
        var (id, username) = await SeedUserAsync();
        await EnrollAsync(username);
        (await StoredSecretAsync(id)).Should().NotBeNull();

        var response = await ResetAsync(await AdminAsync(), id);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadFromJsonAsync<MfaModels.MessageResponse>())!.Message
            .Should().Be("Two-factor authentication was reset.");
    }

    [Fact]
    public async Task Resetting_without_a_token_answers_401()
    {
        var (id, _) = await SeedUserAsync();

        var response = await ResetAsync(Client(), id);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Resetting_without_the_manage_users_capability_answers_403()
    {
        var (id, username) = await SeedUserAsync();
        await EnrollAsync(username);
        var plain = Client(await _factory.StoredUserTokenAsync("User"));

        var response = await ResetAsync(plain, id);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StoredSecretAsync(id)).Should().NotBeNull("a refused call changes nothing");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_or_blank_reason_answers_400(string? reason)
    {
        var (id, username) = await SeedUserAsync();
        await EnrollAsync(username);

        var response = await ResetAsync(await AdminAsync(), id, reason);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StoredSecretAsync(id)).Should().NotBeNull("a refused call changes nothing");
    }

    [Fact]
    public async Task A_reason_over_500_characters_answers_400_and_500_is_accepted()
    {
        var (id, username) = await SeedUserAsync();
        await EnrollAsync(username);
        var admin = await AdminAsync();

        var tooLong = await ResetAsync(admin, id, new string('x', 501));
        var atLimit = await ResetAsync(admin, id, new string('x', 500));

        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        atLimit.StatusCode.Should().Be(HttpStatusCode.OK, await atLimit.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_reset_removes_the_secret_and_recovery_codes_and_status_reports_disabled()
    {
        var (id, username) = await SeedUserAsync();
        await EnrollAsync(username);
        (await StoredSecretAsync(id))!.RecoveryCodeHashes.Should().HaveCount(10);

        (await ResetAsync(await AdminAsync(), id)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await StoredSecretAsync(id)).Should().BeNull();
        (await StatusEnabledAsync(id, username)).Should().BeFalse();
    }

    [Fact]
    public async Task The_reset_removes_a_setup_that_was_never_confirmed()
    {
        var (id, username) = await SeedUserAsync();
        var client = Client((await LoginAsync(username)).Token);
        (await client.PostAsync("/api/auth/mfa/setup", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await StoredSecretAsync(id)).Should().NotBeNull();

        (await ResetAsync(await AdminAsync(), id)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await StoredSecretAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task A_user_whose_role_requires_MFA_must_enroll_again_at_the_next_sign_in()
    {
        var (id, username) = await SeedUserAsync(requireMfa: true);
        await EnrollAsync(username);
        (await LoginAsync(username)).RequiresMfa.Should().BeTrue("the control: enrolled, so a code is asked for");

        (await ResetAsync(await AdminAsync(), id)).StatusCode.Should().Be(HttpStatusCode.OK);

        var login = await LoginAsync(username);
        login.RequiresMfa.Should().BeFalse();
        login.MfaEnrollmentRequired.Should().BeTrue();
    }

    [Fact]
    public async Task A_user_with_no_MFA_gets_200_and_the_reset_can_be_repeated()
    {
        var (id, _) = await SeedUserAsync();
        var admin = await AdminAsync();

        var first = await ResetAsync(admin, id);
        var second = await ResetAsync(admin, id);

        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        second.StatusCode.Should().Be(HttpStatusCode.OK, await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_reset_revokes_the_users_refresh_tokens()
    {
        var (id, username) = await SeedUserAsync();
        var login = await LoginAsync(username);
        login.RequiresMfa.Should().BeFalse();
        var before = await Client().PostAsJsonAsync("/api/auth/refresh",
            new barakoCMS.Features.Auth.Refresh.Request { RefreshToken = login.RefreshToken });
        before.StatusCode.Should().Be(HttpStatusCode.OK, "the control: the token works until the reset");
        var rotated = (await before.Content.ReadFromJsonAsync<barakoCMS.Features.Auth.Refresh.Response>())!.RefreshToken;
        rotated.Should().NotBeNullOrEmpty();

        (await ResetAsync(await AdminAsync(), id)).StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await Client().PostAsJsonAsync("/api/auth/refresh",
            new barakoCMS.Features.Auth.Refresh.Request { RefreshToken = rotated });
        after.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_access_token_issued_before_the_reset_is_refused_after_it()
    {
        var (id, username) = await SeedUserAsync();
        var token = (await LoginAsync(username)).Token;
        (await Client(token).GetAsync("/api/auth/mfa/status")).StatusCode
            .Should().Be(HttpStatusCode.OK, "the control: the token works until the reset");
        await Task.Delay(1100);

        (await ResetAsync(await AdminAsync(), id)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Client(token).GetAsync("/api/auth/mfa/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_reset_writes_an_audit_row_naming_the_admin_the_target_and_the_reason()
    {
        var (id, username) = await SeedUserAsync();
        await EnrollAsync(username);
        var adminToken = await _factory.StoredUserTokenAsync("SuperAdmin");
        var adminId = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(adminToken).Claims.First(c => c.Type == "UserId").Value);
        var reason = $"ticket {Guid.NewGuid():N}";

        (await ResetAsync(Client(adminToken), id, reason)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IQuerySession>().Query<AuditEvent>()
            .Where(e => e.Action == "auth.mfa.reset" && e.TargetId == id.ToString())
            .ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].ActorUserId.Should().Be(adminId);
        rows[0].TargetType.Should().Be("User");
        rows[0].Metadata.Should().NotBeNull();
        rows[0].Metadata!["reason"].ToString().Should().Be(reason);
    }

    [Fact]
    public async Task An_administrator_cannot_reset_their_own_MFA_here()
    {
        var adminToken = await _factory.StoredUserTokenAsync("SuperAdmin");
        var adminId = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(adminToken).Claims.First(c => c.Type == "UserId").Value);

        var response = await ResetAsync(Client(adminToken), adminId);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Use your own two-factor settings to turn off MFA.");
    }

    [Fact]
    public async Task An_unknown_user_answers_404()
    {
        var response = await ResetAsync(await AdminAsync(), Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_user_who_is_not_a_member_of_the_callers_tenant_answers_404()
    {
        var tenantA = $"club-{Guid.NewGuid():N}";
        var tenantB = $"club-{Guid.NewGuid():N}";
        var (inA, usernameA) = await SeedUserAsync();
        var (inB, usernameB) = await SeedUserAsync();
        await EnrollAsync(usernameA);
        await EnrollAsync(usernameB);
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            foreach (var slug in new[] { tenantA, tenantB })
                s.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
            s.Store(new Membership { Id = Guid.NewGuid(), UserId = inA, TenantSlug = tenantA, Status = MembershipStatus.Active });
            s.Store(new Membership { Id = Guid.NewGuid(), UserId = inB, TenantSlug = tenantB, Status = MembershipStatus.Active });
            await s.SaveChangesAsync();
        }
        var admin = await AdminAsync(tenantA);

        var other = await ResetAsync(admin, inB);
        var own = await ResetAsync(admin, inA);

        other.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await StoredSecretAsync(inB)).Should().NotBeNull("the other tenant's user keeps their MFA");
        own.StatusCode.Should().Be(HttpStatusCode.OK, await own.Content.ReadAsStringAsync());
        (await StoredSecretAsync(inA)).Should().BeNull();
    }

    private async Task<Guid> StoreUserAsync(params Guid[] roleIds)
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        var username = $"mfar-{id:N}";
        s.Store(new User
        {
            Id = id,
            Username = username,
            Email = $"{username}@test.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            RoleIds = [.. roleIds],
        });
        await s.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> StoreCapabilityRoleAsync(params string[] capabilities)
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        s.Store(new Role { Id = id, Name = $"cap-{Guid.NewGuid():N}"[..20], SystemCapabilities = [.. capabilities] });
        await s.SaveChangesAsync();
        return id;
    }

    private async Task GiveEnrollmentAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        s.Store(new MfaSecret { Id = userId, EncryptedSecret = "x", Enabled = true, RecoveryCodeHashes = ["h"] });
        await s.SaveChangesAsync();
    }

    private HttpClient As(Guid userId, string? tenant = null) =>
        Client(_factory.CreateToken([], userId.ToString()), tenant);

    [Fact]
    public async Task A_member_whose_manage_users_comes_only_from_a_tenant_membership_answers_403()
    {
        var slug = $"club-{Guid.NewGuid():N}";
        var memberRole = await StoreCapabilityRoleAsync("manage_users");
        var caller = await StoreUserAsync();
        var target = await StoreUserAsync();
        await GiveEnrollmentAsync(target);
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            s.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
            s.Store(new Membership { Id = Guid.NewGuid(), UserId = caller, TenantSlug = slug, Status = MembershipStatus.Active, RoleIds = [memberRole] });
            s.Store(new Membership { Id = Guid.NewGuid(), UserId = target, TenantSlug = slug, Status = MembershipStatus.Active });
            await s.SaveChangesAsync();
        }

        var response = await ResetAsync(As(caller, slug), target);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StoredSecretAsync(target)).Should().NotBeNull("a refused call changes nothing");
    }

    [Fact]
    public async Task An_administrator_who_is_not_a_SuperAdmin_cannot_reset_a_SuperAdmin()
    {
        var caller = await StoreUserAsync(await StoreCapabilityRoleAsync("manage_users"));
        var target = await StoreUserAsync(SystemRoles.SuperAdminRoleId);
        await GiveEnrollmentAsync(target);

        var response = await ResetAsync(As(caller), target);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("only a platform administrator (SuperAdmin)");
        (await StoredSecretAsync(target)).Should().NotBeNull();
    }

    [Fact]
    public async Task An_administrator_who_is_not_a_SuperAdmin_cannot_reset_a_holder_of_a_platform_capability_role()
    {
        var caller = await StoreUserAsync(await StoreCapabilityRoleAsync("manage_users"));
        var target = await StoreUserAsync(await StoreCapabilityRoleAsync("manage_roles"));
        await GiveEnrollmentAsync(target);

        var response = await ResetAsync(As(caller), target);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StoredSecretAsync(target)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_SuperAdmin_can_reset_another_SuperAdmin()
    {
        var caller = await StoreUserAsync(SystemRoles.SuperAdminRoleId);
        var target = await StoreUserAsync(SystemRoles.SuperAdminRoleId);
        await GiveEnrollmentAsync(target);

        var response = await ResetAsync(As(caller), target);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await StoredSecretAsync(target)).Should().BeNull();
    }

    [Fact]
    public async Task A_token_for_no_stored_user_answers_403_and_changes_nothing()
    {
        var (id, username) = await SeedUserAsync();
        await EnrollAsync(username);
        var stray = Client(_factory.CreateToken(["SuperAdmin"]));

        var response = await ResetAsync(stray, id);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StoredSecretAsync(id)).Should().NotBeNull();
    }

    [Fact]
    public async Task An_API_key_acting_as_an_administrator_is_refused()
    {
        var (id, username) = await SeedUserAsync();
        await EnrollAsync(username);
        var adminId = Guid.Parse(new JwtSecurityTokenHandler()
            .ReadJwtToken(await _factory.StoredUserTokenAsync("SuperAdmin")).Claims.First(c => c.Type == "UserId").Value);
        var secret = "bcms_" + Guid.NewGuid().ToString("N");
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            s.Store(new ApiKey
            {
                Id = Guid.NewGuid(), Name = "mfa-reset", KeyHash = ApiKeyService.Hash(secret), Prefix = secret[..12],
                UserId = adminId, TenantSlug = Tenant.DefaultSlug, Scopes = ["content:read", "content:write"],
            });
            await s.SaveChangesAsync();
        }

        var response = await ResetAsync(Client(secret), id);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StoredSecretAsync(id)).Should().NotBeNull();
    }

    [Fact]
    public async Task In_the_default_tenant_no_membership_is_needed()
    {
        var (id, username) = await SeedUserAsync();
        await EnrollAsync(username);

        var response = await ResetAsync(await AdminAsync(Tenant.DefaultSlug), id);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await StoredSecretAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task In_a_tenant_slug_nobody_registered_no_membership_is_needed()
    {
        var (id, username) = await SeedUserAsync();
        await EnrollAsync(username);

        var response = await ResetAsync(await AdminAsync($"club-{Guid.NewGuid():N}"), id);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await StoredSecretAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task A_non_SuperAdmin_cannot_reset_a_user_who_holds_a_platform_role_only_through_a_membership()
    {
        var caller = await StoreUserAsync(await StoreCapabilityRoleAsync("manage_users"));
        var target = await StoreUserAsync();
        var platformRole = await StoreCapabilityRoleAsync("manage_roles");
        await GiveEnrollmentAsync(target);
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            s.Store(new Membership
            {
                Id = Guid.NewGuid(), UserId = target, TenantSlug = $"club-{Guid.NewGuid():N}",
                Status = MembershipStatus.Active, RoleIds = [platformRole],
            });
            await s.SaveChangesAsync();
        }

        var response = await ResetAsync(As(caller), target);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StoredSecretAsync(target)).Should().NotBeNull();
    }

    [Fact]
    public async Task With_the_legacy_fallback_on_a_role_named_SuperAdmin_makes_its_holder_a_platform_user()
    {
        var caller = await StoreUserAsync(await StoreCapabilityRoleAsync("manage_users"));
        Guid namedRole;
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            namedRole = Guid.NewGuid();
            s.Store(new Role { Id = namedRole, Name = "SuperAdmin", TenantSlug = $"club-{Guid.NewGuid():N}", SystemCapabilities = [] });
            await s.SaveChangesAsync();
        }
        try
        {
            var target = await StoreUserAsync(namedRole);
            await GiveEnrollmentAsync(target);
            var legacy = _factory.WithSetting(barakoCMS.Infrastructure.Auth.CapabilityGateProcessor.LegacyRoleFallbackKey, "true");
            var client = legacy.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _factory.CreateToken([], caller.ToString()));

            var response = await ResetAsync(client, target);

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await StoredSecretAsync(target)).Should().NotBeNull();
        }
        finally
        {
            // Other classes find the admin role by the name SuperAdmin, and would pick this empty one.
            using var scope = _factory.Services.CreateScope();
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            s.Delete<Role>(namedRole);
            await s.SaveChangesAsync();
        }
    }
}

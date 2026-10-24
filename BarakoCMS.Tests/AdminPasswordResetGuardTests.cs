using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

[Collection("Sequential")]
public class AdminPasswordResetGuardTests
{
    private const string OldPassword = "OldPassw0rd!x";
    private const string NewPassword = "ResetByAdm1n!";
    private static int _ipCounter;

    private readonly IntegrationTestFixture _factory;
    private readonly string _ip;

    public AdminPasswordResetGuardTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        var n = Interlocked.Increment(ref _ipCounter);
        _ip = $"198.51.100.{(n + 120) % 250 + 1}";
    }

    private HttpClient Client(string bearer, string? tenant = null)
    {
        var c = _factory
            .WithWebHostBuilder(b => b.ConfigureServices(s =>
                s.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, TestRemoteIpFilter>()))
            .CreateClient();
        c.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, _ip);
        if (tenant is not null) c.DefaultRequestHeaders.Add("X-Tenant", tenant);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return c;
    }

    private HttpClient As(Guid userId, string? tenant = null) =>
        Client(_factory.CreateToken([], userId.ToString()), tenant);

    private async Task<Guid> StoreUserAsync(params Guid[] roleIds)
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        s.Store(new User
        {
            Id = id,
            Username = $"pwr-{id:N}",
            Email = $"pwr-{id:N}@test.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(OldPassword),
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

    private async Task<bool> StillHasOldPasswordAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<User>(userId);
        return BCrypt.Net.BCrypt.Verify(OldPassword, user!.PasswordHash);
    }

    private static Task<HttpResponseMessage> ResetAsync(HttpClient client, Guid userId) =>
        client.PostAsJsonAsync($"/api/users/{userId}/password", new { NewPassword });

    [Fact]
    public async Task A_member_whose_manage_users_comes_only_from_a_tenant_membership_is_refused()
    {
        var slug = $"club-{Guid.NewGuid():N}";
        var memberRole = await StoreCapabilityRoleAsync("manage_users");
        var caller = await StoreUserAsync();
        var target = await StoreUserAsync();
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
        (await StillHasOldPasswordAsync(target)).Should().BeTrue("a refused call changes nothing");
    }

    [Fact]
    public async Task A_non_SuperAdmin_cannot_reset_a_SuperAdmin()
    {
        var caller = await StoreUserAsync(await StoreCapabilityRoleAsync("manage_users"));
        var target = await StoreUserAsync(SystemRoles.SuperAdminRoleId);

        var response = await ResetAsync(As(caller), target);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StillHasOldPasswordAsync(target)).Should().BeTrue();
    }

    [Fact]
    public async Task A_user_outside_the_callers_tenant_answers_404()
    {
        var tenantA = $"club-{Guid.NewGuid():N}";
        var tenantB = $"club-{Guid.NewGuid():N}";
        var inB = await StoreUserAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            foreach (var slug in new[] { tenantA, tenantB })
                s.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
            s.Store(new Membership { Id = Guid.NewGuid(), UserId = inB, TenantSlug = tenantB, Status = MembershipStatus.Active });
            await s.SaveChangesAsync();
        }
        var admin = Client(await _factory.StoredUserTokenAsync("SuperAdmin"), tenantA);

        var response = await ResetAsync(admin, inB);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await StillHasOldPasswordAsync(inB)).Should().BeTrue();
    }

    [Fact]
    public async Task An_administrator_cannot_reset_their_own_password_here()
    {
        var token = await _factory.StoredUserTokenAsync("SuperAdmin");
        var self = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(token).Claims.First(c => c.Type == "UserId").Value);

        var response = await ResetAsync(Client(token), self);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_SuperAdmin_resets_a_user_and_the_reset_is_audited()
    {
        var target = await StoreUserAsync();
        var token = await _factory.StoredUserTokenAsync("SuperAdmin");
        var adminId = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(token).Claims.First(c => c.Type == "UserId").Value);

        var response = await ResetAsync(Client(token), target);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await StillHasOldPasswordAsync(target)).Should().BeFalse();

        using var scope = _factory.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IQuerySession>().Query<AuditEvent>()
            .Where(e => e.Action == "auth.password.reset" && e.TargetId == target.ToString())
            .ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].ActorUserId.Should().Be(adminId);
    }
}

using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Overview;

/// <summary><see cref="ICapabilityResolver"/>: capabilities with no request, by the gate's rules.</summary>
[Collection("Integration1")]
public class CapabilityResolverTests
{
    private readonly IntegrationTestFixture _fixture;

    public CapabilityResolverTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDocumentStore Store => _fixture.Services.GetRequiredService<IDocumentStore>();

    private ICapabilityResolver Resolver => _fixture.Services.GetRequiredService<ICapabilityResolver>();

    private async Task<string> TenantAsync(TenantStatus status = TenantStatus.Active)
    {
        var slug = "resolver-" + Guid.NewGuid().ToString("N")[..16];
        await using var session = Store.LightweightSession();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, Status = status });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    private async Task<Role> RoleAsync(string tenant, params string[] capabilities)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = "rs-" + Guid.NewGuid().ToString("N"),
            TenantSlug = tenant,
            SystemCapabilities = [.. capabilities],
        };
        await using var session = Store.LightweightSession();
        session.Store(role);
        await session.SaveChangesAsync(Ct);
        return role;
    }

    private async Task<ServicePrincipal> PrincipalAsync(string tenant, bool disabled, params Role[] roles)
    {
        var principal = new ServicePrincipal
        {
            Id = Guid.NewGuid(),
            TenantSlug = tenant,
            Name = "rs-" + Guid.NewGuid().ToString("N")[..8],
            RoleIds = [.. roles.Select(r => r.Id)],
            DisabledAt = disabled ? DateTime.UtcNow : null,
        };
        await using var session = Store.LightweightSession();
        session.Store(principal);
        await session.SaveChangesAsync(Ct);
        return principal;
    }

    [Fact]
    public async Task Principal_capabilities_match_what_the_gate_would_grant()
    {
        var tenant = await TenantAsync();
        var other = await TenantAsync();
        var a = await RoleAsync(tenant, SystemCapabilities.ViewWorkflowRuns);
        var b = await RoleAsync(tenant, SystemCapabilities.ViewMonitoring, SystemCapabilities.ViewWorkflowRuns);
        var foreign = await RoleAsync(other, SystemCapabilities.ManageEmailSettings);
        var platform = await RoleAsync(tenant, SystemCapabilities.ManageTenants);
        platform.IsPlatform = true;
        var star = await RoleAsync(tenant, SystemCapabilities.All);
        await using (var session = Store.LightweightSession())
        {
            session.Store(platform);
            await session.SaveChangesAsync(Ct);
        }

        var principal = await PrincipalAsync(tenant, false, a, b, foreign, platform, star);

        var held = await Resolver.ForPrincipalAsync(principal.Id, tenant, Ct);

        held.Should().BeEquivalentTo([SystemCapabilities.ViewWorkflowRuns, SystemCapabilities.ViewMonitoring]);
    }

    [Fact]
    public async Task A_disabled_principal_holds_none()
    {
        var tenant = await TenantAsync();
        var role = await RoleAsync(tenant, SystemCapabilities.ViewWorkflowRuns);
        var principal = await PrincipalAsync(tenant, true, role);

        (await Resolver.ForPrincipalAsync(principal.Id, tenant, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_principal_asked_for_in_another_tenant_holds_none()
    {
        var tenant = await TenantAsync();
        var other = await TenantAsync();
        var role = await RoleAsync(tenant, SystemCapabilities.ViewWorkflowRuns);
        var principal = await PrincipalAsync(tenant, false, role);

        (await Resolver.ForPrincipalAsync(principal.Id, other, Ct)).Should().BeEmpty();
        (await Resolver.ForPrincipalAsync(Guid.NewGuid(), tenant, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_principal_of_a_suspended_tenant_holds_none()
    {
        var tenant = await TenantAsync(TenantStatus.Suspended);
        var role = await RoleAsync(tenant, SystemCapabilities.ViewWorkflowRuns);
        var principal = await PrincipalAsync(tenant, false, role);

        (await Resolver.ForPrincipalAsync(principal.Id, tenant, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_user_holds_the_union_of_the_roles_of_the_membership()
    {
        var tenant = await TenantAsync();
        var a = await RoleAsync(tenant, SystemCapabilities.ViewAuditLog);
        var b = await RoleAsync(tenant, SystemCapabilities.ManageEmailSettings);
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "rs-" + Guid.NewGuid().ToString("N")[..12],
            Email = $"{Guid.NewGuid():N}@example.com",
        };
        await using (var session = Store.LightweightSession())
        {
            session.Store(user);
            session.Store(new Membership
            {
                Id = Guid.NewGuid(), UserId = user.Id, TenantSlug = tenant,
                Status = MembershipStatus.Active, RoleIds = [a.Id, b.Id],
            });
            await session.SaveChangesAsync(Ct);
        }

        var held = await Resolver.ForUserAsync(user.Id, tenant, Ct);

        held.Should().BeEquivalentTo([SystemCapabilities.ViewAuditLog, SystemCapabilities.ManageEmailSettings]);
        (await Resolver.ForUserAsync(Guid.NewGuid(), tenant, Ct)).Should().BeEmpty();
    }

    private async Task<Guid> UserAsync(string tenant, MembershipStatus status, Guid[] membershipRoles, Guid[]? globalRoles = null)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "rs-" + Guid.NewGuid().ToString("N")[..12],
            Email = $"{Guid.NewGuid():N}@example.com",
            RoleIds = [.. globalRoles ?? []],
        };
        await using var session = Store.LightweightSession();
        session.Store(user);
        session.Store(new Membership
        {
            Id = Guid.NewGuid(), UserId = user.Id, TenantSlug = tenant,
            Status = status, RoleIds = [.. membershipRoles],
        });
        await session.SaveChangesAsync(Ct);
        return user.Id;
    }

    [Fact]
    public async Task A_super_admin_holds_the_all_capabilities_marker()
    {
        var tenant = await TenantAsync();
        await using (var session = Store.LightweightSession())
        {
            if (await session.LoadAsync<Role>(SystemRoles.SuperAdminRoleId, Ct) is null)
            {
                session.Store(new Role
                {
                    Id = SystemRoles.SuperAdminRoleId, Name = "SuperAdmin", IsPlatform = true,
                    SystemCapabilities = [SystemCapabilities.All],
                });
                await session.SaveChangesAsync(Ct);
            }
        }

        var userId = await UserAsync(tenant, MembershipStatus.Active, [], [SystemRoles.SuperAdminRoleId]);

        (await Resolver.ForUserAsync(userId, tenant, Ct)).Should().BeEquivalentTo([SystemCapabilities.All]);
    }

    [Fact]
    public async Task A_user_of_a_suspended_tenant_holds_none()
    {
        var tenant = await TenantAsync(TenantStatus.Suspended);
        var role = await RoleAsync(tenant, SystemCapabilities.ViewWorkflowRuns);
        var userId = await UserAsync(tenant, MembershipStatus.Active, [role.Id]);

        (await Resolver.ForUserAsync(userId, tenant, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_deleted_role_grants_nothing_to_a_user_or_a_principal()
    {
        var tenant = await TenantAsync();
        var live = await RoleAsync(tenant, SystemCapabilities.ViewAuditLog);
        var deleted = await RoleAsync(tenant, SystemCapabilities.ViewWorkflowRuns);
        deleted.DeletedAt = DateTime.UtcNow;
        await using (var session = Store.LightweightSession())
        {
            session.Store(deleted);
            await session.SaveChangesAsync(Ct);
        }

        var userId = await UserAsync(tenant, MembershipStatus.Active, [live.Id, deleted.Id]);
        var principal = await PrincipalAsync(tenant, false, live, deleted);

        (await Resolver.ForUserAsync(userId, tenant, Ct)).Should().BeEquivalentTo([SystemCapabilities.ViewAuditLog]);
        (await Resolver.ForPrincipalAsync(principal.Id, tenant, Ct)).Should().BeEquivalentTo([SystemCapabilities.ViewAuditLog]);
    }

    [Theory]
    [InlineData(MembershipStatus.Suspended)]
    [InlineData(MembershipStatus.Removed)]
    public async Task A_user_whose_only_membership_is_not_active_holds_none(MembershipStatus status)
    {
        var tenant = await TenantAsync();
        var role = await RoleAsync(tenant, SystemCapabilities.ViewWorkflowRuns);
        var userId = await UserAsync(tenant, status, [role.Id]);

        (await Resolver.ForUserAsync(userId, tenant, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_principal_never_holds_a_platform_capability_or_the_all_marker()
    {
        var tenant = await TenantAsync();
        var mixed = await RoleAsync(
            tenant, SystemCapabilities.All, SystemCapabilities.ManageTenants, SystemCapabilities.ViewMonitoring);
        var platform = await RoleAsync(tenant, SystemCapabilities.ViewAuditLog);
        platform.IsPlatform = true;
        await using (var session = Store.LightweightSession())
        {
            session.Store(platform);
            await session.SaveChangesAsync(Ct);
        }

        var principal = await PrincipalAsync(tenant, false, mixed, platform);

        (await Resolver.ForPrincipalAsync(principal.Id, tenant, Ct)).Should().BeEquivalentTo([SystemCapabilities.ViewMonitoring]);
    }
}

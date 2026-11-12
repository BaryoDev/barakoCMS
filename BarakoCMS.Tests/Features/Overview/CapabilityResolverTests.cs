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
}

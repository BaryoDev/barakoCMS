using barakoCMS.Core;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Roles;

/// <summary>
/// A role name in a field's VisibleToRoles resolves to a role the writing tenant reaches, since a
/// name is unique only within one owner and role-tenancy.sql copies a role under the same name (#913).
/// </summary>
[Collection("Integration1")]
public class RoleReferenceTenancyTests
{
    private readonly IntegrationTestFixture _fixture;

    public RoleReferenceTenancyTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Slug(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..16];

    private async Task StoreAsync(params Role[] roles)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(roles);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<List<string>> ResolvedInAsync(string tenantSlug, string name)
    {
        using var scope = _fixture.Services.CreateScopeForTenant(tenantSlug);
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var field = new FieldDefinition { Name = "notes", Type = "string", VisibleToRoles = [name] };
        await RoleReferences.ToIdsAsync(session, [field], Ct);
        return field.VisibleToRoles;
    }

    private static Role RoleIn(string? tenant, string name, bool platform = false) =>
        new() { Id = Guid.NewGuid(), Name = name, Description = "d", TenantSlug = tenant, IsPlatform = platform };

    [Fact]
    public async Task Two_tenants_holding_the_same_role_name_each_resolve_it_to_their_own_role()
    {
        var (a, b) = (Slug("rra"), Slug("rrb"));
        var name = $"Editor-{Guid.NewGuid():N}"[..16];
        var inA = RoleIn(a, name);
        var inB = RoleIn(b, name);
        await StoreAsync(inA, inB);

        (await ResolvedInAsync(a, name)).Should().Equal(inA.Id.ToString());
        (await ResolvedInAsync(b, name)).Should().Equal(inB.Id.ToString());
    }

    [Fact]
    public async Task A_tenants_own_role_wins_over_a_platform_role_of_the_same_name()
    {
        var a = Slug("rrp");
        var name = $"Viewer-{Guid.NewGuid():N}"[..16];
        var platform = RoleIn(null, name, platform: true);
        var own = RoleIn(a, name);
        await StoreAsync(platform, own);

        (await ResolvedInAsync(a, name)).Should().Equal(own.Id.ToString());
        (await ResolvedInAsync(Slug("rrq"), name)).Should().Equal([platform.Id.ToString()],
            "a tenant without its own role of that name gets the platform role");
    }

    [Fact]
    public async Task A_name_only_another_tenant_holds_is_kept_as_a_name()
    {
        var name = $"Other-{Guid.NewGuid():N}"[..16];
        await StoreAsync(RoleIn(Slug("rro"), name));

        (await ResolvedInAsync(Slug("rrn"), name)).Should().Equal(name);
    }
}

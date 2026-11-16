using System.Net;
using FluentAssertions;
using Marten;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Config;

/// <summary>
/// A role stored with two rules that name one content type (two spellings of it) does not stop the
/// config engine from planning or applying the permission rules.
/// </summary>
[Collection("Integration1")]
public class PermissionRuleDuplicateSlugTests
{
    private readonly ConfigCallers _callers;

    public PermissionRuleDuplicateSlugTests(IntegrationTestFixture fixture) => _callers = new ConfigCallers(fixture);

    private async Task<(HttpClient Client, string Role, string Type)> SeedAsync()
    {
        var tenant = await _callers.TenantAsync();
        var type = $"dup{Guid.NewGuid():n}"[..12];
        await _callers.StoreInTenantAsync(tenant, ConfigCallers.Type(type, ("Title", "string")));
        var role = $"Dup Clerk {Guid.NewGuid():n}"[..20];
        await _callers.StoreInTenantAsync(null, new Role
        {
            Id = Guid.NewGuid(),
            Name = role,
            IsPlatform = true,
            Permissions =
            [
                new ContentTypePermission { ContentTypeSlug = type, Read = new PermissionRule { Enabled = true } },
                new ContentTypePermission { ContentTypeSlug = type.ToUpperInvariant() + " ", Read = new PermissionRule { Enabled = true } },
            ],
        });
        return (await _callers.SuperAdminAsync(tenant), role, type);
    }

    [Fact]
    public async Task A_plan_over_a_role_holding_two_spellings_of_one_type_answers()
    {
        var (client, role, type) = await SeedAsync();

        var (response, plan) = await ConfigCallers.PlanAsync(client, new { version = 1, permissionRules = new Dictionary<string, object>() });

        response.StatusCode.Should().Be(HttpStatusCode.OK, plan.ToString());
        ConfigCallers.Changes(plan).Should().Contain($"permissionRules/{role}/{type} destructive");
    }

    [Fact]
    public async Task An_apply_over_a_role_holding_two_spellings_of_one_type_answers()
    {
        var (client, role, _) = await SeedAsync();

        var (response, body) = await ConfigCallers.ApplyAsync(
            client, new { version = 1, permissionRules = new Dictionary<string, object>() }, allowDestructive: true);

        response.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());

        var stored = await _callers.InTenantAsync<List<Role>>(null,
            async session => (await session.Query<Role>().Where(r => r.Name == role).ToListAsync()).ToList());
        stored.Should().HaveCount(1);
        stored![0].Permissions.Should().BeEmpty();
    }
}

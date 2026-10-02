using barakoCMS.Data;
using barakoCMS.Extensions;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The core seeds SuperAdmin, Admin and User. HR belongs to the attendance demo and comes with it.
/// </summary>
/// <remarks>
/// Every host used to be seeded with an HR role it could not delete, production included, because
/// role seeding ran outside the demo content gate. A database seeded then still holds that role,
/// and its holders read Sensitive fields because of its name. So the other half of this is that
/// such a role is kept, stays undeletable, and is given the capability that replaced the name.
///
/// The seeding test builds a host of its own on an empty database in the fixture's Postgres. The
/// fixture's own database cannot answer "exactly these roles": every other test class puts roles in
/// it. In the Sequential collection and on the fixture's JWT key for the reason
/// <see cref="EnabledModuleEndpointTests"/> gives.
/// </remarks>
[Collection("Sequential")]
public sealed class SeededRolesTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task A_fresh_install_is_seeded_with_SuperAdmin_Admin_and_User_and_with_HR_only_beside_the_demo_content()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = $"seeded_roles_{Guid.NewGuid():N}"[..30];

        await ExecuteAsync($"CREATE DATABASE {database}", ct);
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] =
                    new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }.ConnectionString,
                ["DATABASE_URL"] = string.Empty,
                ["JWT:Key"] = IntegrationTestFixture.JwtKey,
                ["JWT:Issuer"] = "BarakoTest",
                ["JWT:Audience"] = "BarakoClient",
                ["InitialAdmin:Username"] = "admin",
                ["InitialAdmin:Password"] = $"Test-{Guid.NewGuid():N}!",
                ["Seed:DemoContent"] = "false",
            });
            builder.Services.AddBarakoCMS(builder.Configuration);

            await using var app = builder.Build();
            await app.ApplyMartenSchemaAsync();
            var store = app.Services.GetRequiredService<IDocumentStore>();

            await DataSeeder.SeedAsync(app);

            var withoutDemo = await RolesAsync(store, ct);
            withoutDemo.Should().HaveCount(3, "demo content is off, so nothing but the core roles is seeded");
            withoutDemo.Select(r => r.Name).Should().BeEquivalentTo(new[] { "SuperAdmin", "Admin", "User" });

            await using (var read = store.QuerySession())
            {
                var users = await read.Query<User>().ToListAsync(ct);
                var held = users.SelectMany(u => u.RoleIds).Distinct().ToList();
                held.Should().NotBeEmpty("the initial admin is seeded and holds roles");
                held.Should().BeSubsetOf(withoutDemo.Select(r => r.Id),
                    "no account is seeded holding a role that was not");
            }

            app.Configuration["Seed:DemoContent"] = "true";
            await DataSeeder.SeedAsync(app);

            var withDemo = await RolesAsync(store, ct);
            withDemo.Should().HaveCount(4, "the demo content brings its HR role with it");
            var hr = withDemo.Single(r => r.Id == DataSeeder.DemoHrRoleId);
            hr.Name.Should().Be("HR");
            hr.SystemCapabilities.Should().HaveCount(1);
            hr.SystemCapabilities.Should().Equal(SystemCapabilities.ViewSensitive);

            // What a database seeded before this release holds: the role, with nothing granted.
            await using (var write = store.LightweightSession())
            {
                hr.SystemCapabilities = [];
                write.Store(hr);
                await write.SaveChangesAsync(ct);
            }

            app.Configuration["Seed:DemoContent"] = "false";
            await DataSeeder.SeedAsync(app);

            var upgraded = await RolesAsync(store, ct);
            upgraded.Should().HaveCount(4, "a role already stored is kept when the demo content is off");
            var kept = upgraded.Single(r => r.Id == DataSeeder.DemoHrRoleId);
            kept.SystemCapabilities.Should().HaveCount(1);
            kept.SystemCapabilities.Should().Equal(
                new[] { SystemCapabilities.ViewSensitive },
                "its holders read Sensitive fields by its name before, and by this capability now");
            SystemRoles.Contains(kept.Id).Should().BeTrue("and it still cannot be deleted");
        }
        finally
        {
            await ExecuteAsync($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", CancellationToken.None);
        }
    }

    private static async Task<IReadOnlyList<Role>> RolesAsync(IDocumentStore store, CancellationToken ct)
    {
        await using var read = store.QuerySession();
        return await read.Query<Role>().ToListAsync(ct);
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public void The_seeded_HR_role_gains_view_sensitive_once_and_keeps_what_it_held()
    {
        var role = new Role
        {
            Id = DataSeeder.DemoHrRoleId,
            Name = "HR",
            SystemCapabilities = [SystemCapabilities.ViewAuditLog],
        };

        DataSeeder.GrantSensitiveToSeededHr(role).Should().BeTrue();
        role.SystemCapabilities.Should().HaveCount(2);
        role.SystemCapabilities.Should().Equal(SystemCapabilities.ViewAuditLog, SystemCapabilities.ViewSensitive);

        DataSeeder.GrantSensitiveToSeededHr(role).Should().BeFalse("it holds the capability now");
        role.SystemCapabilities.Should().HaveCount(2);
    }

    [Fact]
    public void A_seeded_HR_role_that_was_renamed_is_left_alone()
    {
        var role = new Role { Id = DataSeeder.DemoHrRoleId, Name = "People Operations" };

        DataSeeder.GrantSensitiveToSeededHr(role).Should().BeFalse(
            "renamed away from HR, its holders had stopped reading Sensitive fields already, and "
          + "granting the capability here would hand that back unasked");
        role.SystemCapabilities.Should().BeEmpty();
    }

    [Fact]
    public void A_role_of_the_operators_own_called_HR_is_left_alone()
    {
        var role = new Role { Id = Guid.NewGuid(), Name = "HR" };

        DataSeeder.GrantSensitiveToSeededHr(role).Should().BeFalse(
            "the name is free to use now, and a name grants nothing");
        role.SystemCapabilities.Should().BeEmpty();
    }

    [Fact]
    public void A_seeded_HR_role_holding_the_wildcard_is_left_alone()
    {
        var role = new Role { Id = DataSeeder.DemoHrRoleId, Name = "HR", SystemCapabilities = [SystemCapabilities.All] };

        DataSeeder.GrantSensitiveToSeededHr(role).Should().BeFalse();
        role.SystemCapabilities.Should().HaveCount(1);
        role.SystemCapabilities.Should().Equal(SystemCapabilities.All);
    }

    [Fact]
    public void The_demo_HR_id_is_the_one_older_databases_hold_and_is_still_a_system_role()
    {
        SystemRoles.Contains(DataSeeder.DemoHrRoleId).Should().BeTrue(
            "a database that already holds the role must still refuse to delete it");

#pragma warning disable CS0618
        DataSeeder.HRRoleId.Should().Be(DataSeeder.DemoHrRoleId);
        SystemRoles.HRRoleId.Should().Be(DataSeeder.DemoHrRoleId);
#pragma warning restore CS0618
    }
}

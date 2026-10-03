using barakoCMS.Data;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The core seeder finds a seeded role by its fixed id, so a role an operator renamed survives the
/// next start with its name, its permissions and its capabilities.
/// </summary>
/// <remarks>
/// It looked by name. After a rename it found no role called Admin and stored a new one under the
/// seeded id, which replaced the renamed role and emptied its permissions on every start.
/// </remarks>
[Collection("Sequential")]
public class SystemRoleRenameTests
{
    private readonly IntegrationTestFixture _factory;

    public SystemRoleRenameTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_renamed_admin_role_keeps_its_name_permissions_and_capabilities_through_the_seeder()
    {
        var original = await LoadAsync(SystemRoles.AdminRoleId);
        original.Should().NotBeNull("the fixture seeds the Admin role under its fixed id");

        var renamed = $"Site Admin {Guid.NewGuid():N}";

        try
        {
            using (var scope = _factory.Services.CreateScope())
            {
                var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
                var role = await session.LoadAsync<Role>(SystemRoles.AdminRoleId, Ct);
                role!.Name = renamed;
                role.Permissions =
                [
                    new ContentTypePermission { ContentTypeSlug = "renamed-admin-marker", Read = new PermissionRule { Enabled = true } },
                ];
                role.SystemCapabilities = [.. role.SystemCapabilities, "kept_by_the_operator"];
                session.Store(role);
                await session.SaveChangesAsync(Ct);
            }

            using (var scope = _factory.Services.CreateScope())
            {
                var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
                await DataSeeder.SeedRolesAsync(session);
            }

            var after = await LoadAsync(SystemRoles.AdminRoleId);
            after.Should().NotBeNull();
            after!.Name.Should().Be(renamed);
            after.Permissions.Should().ContainSingle()
                .Which.ContentTypeSlug.Should().Be("renamed-admin-marker");
            after.SystemCapabilities.Should().NotBeEmpty();
            after.SystemCapabilities.Should().Contain("kept_by_the_operator");
            after.SystemCapabilities.Should().Contain(SystemCapabilities.DefaultsFor("Admin"),
                "the seeded Admin's defaults still reach it under its new name");

            using (var scope = _factory.Services.CreateScope())
            {
                var byName = await scope.ServiceProvider.GetRequiredService<IQuerySession>()
                    .Query<Role>().FirstOrDefaultAsync(r => r.Name == "Admin", Ct);
                byName.Should().BeNull("no second role was made under the seeded name");
            }
        }
        finally
        {
            using var scope = _factory.Services.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(original!);
            await session.SaveChangesAsync(CancellationToken.None);
        }
    }

    private async Task<Role?> LoadAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Role>(id, Ct);
    }
}

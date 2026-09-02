using FluentAssertions;
using barakoCMS.Data;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The upgrade path for a database seeded before capabilities existed. Its SuperAdmin and Admin
/// documents carry an empty <see cref="Role.SystemCapabilities"/>, and the seeder fills them in on
/// the next start so an operator can see and edit what those roles hold.
/// </summary>
public class CapabilityDefaultsBackfillTests
{
    [Fact]
    public void An_existing_system_role_with_no_capabilities_is_backfilled()
    {
        var role = new Role { Id = SystemRoles.SuperAdminRoleId, Name = "SuperAdmin" };

        DataSeeder.ApplyCapabilityDefaults(role).Should().BeTrue();

        role.SystemCapabilities.Should().Equal(SystemCapabilities.All);
    }

    [Fact]
    public void Admin_is_backfilled_with_the_surfaces_it_already_reached()
    {
        var role = new Role { Id = SystemRoles.AdminRoleId, Name = "Admin" };

        DataSeeder.ApplyCapabilityDefaults(role).Should().BeTrue();

        role.SystemCapabilities.Should().NotBeEmpty();
        role.SystemCapabilities.Should().Contain(SystemCapabilities.ManageTenantMembers);
        role.SystemCapabilities.Should().Contain(SystemCapabilities.ManageUserMembership);
        role.SystemCapabilities.Should().Contain(SystemCapabilities.ManageUserGroups);
        role.SystemCapabilities.Should().NotContain(SystemCapabilities.ManageRoles);
        role.SystemCapabilities.Should().NotContain(SystemCapabilities.ManageUsers,
            "the user list and the password reset were SuperAdmin only, and a backfill must not widen that");
    }

    /// <summary>
    /// A restart must not undo an operator's curation. Once a role holds any capability, the stored
    /// list is the answer, including a list an operator deliberately narrowed.
    /// </summary>
    [Fact]
    public void A_curated_system_role_is_left_alone()
    {
        var role = new Role
        {
            Id = SystemRoles.AdminRoleId,
            Name = "Admin",
            SystemCapabilities = [SystemCapabilities.ManageRoles],
        };

        DataSeeder.ApplyCapabilityDefaults(role).Should().BeFalse();

        role.SystemCapabilities.Should().Equal(SystemCapabilities.ManageRoles);
    }

    [Fact]
    public void A_role_someone_created_gets_nothing_from_its_name()
    {
        var role = new Role { Id = Guid.NewGuid(), Name = "Admin " + Guid.NewGuid() };

        DataSeeder.ApplyCapabilityDefaults(role).Should().BeFalse();

        role.SystemCapabilities.Should().BeEmpty();
    }
}

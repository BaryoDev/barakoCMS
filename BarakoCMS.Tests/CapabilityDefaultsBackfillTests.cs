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
    /// A capability added to the vocabulary after a deployment upgraded still reaches its Admin.
    /// </summary>
    /// <remarks>
    /// This is the case that made the old rule wrong. It filled only an empty list, so a deployment
    /// upgraded once had an Admin whose count was not zero, and every area migrated afterwards never
    /// arrived. Nothing broke while <c>Auth:LegacyRoleFallback</c> was on, because the gate still
    /// honoured the role names. Turning the fallback off, which is the point of the migration, is
    /// where that Admin would have silently lost every later area.
    /// </remarks>
    [Fact]
    public void A_default_added_after_an_upgrade_still_reaches_an_existing_role()
    {
        // An Admin as an earlier version left it: real names, and not the whole set.
        var role = new Role
        {
            Id = SystemRoles.AdminRoleId,
            Name = "Admin",
            SystemCapabilities = [SystemCapabilities.ManageTenantMembers, SystemCapabilities.ManageUserMembership],
        };

        DataSeeder.ApplyCapabilityDefaults(role).Should().BeTrue();

        role.SystemCapabilities.Should().BeEquivalentTo(SystemCapabilities.DefaultsFor("Admin"),
            "an upgraded role ends up holding what a freshly seeded one does");
        role.SystemCapabilities.Should().OnlyHaveUniqueItems(
            "the names it already had must not be added a second time");
    }

    /// <summary>
    /// A role already holding its whole default set is not rewritten, so a restart is not a write.
    /// </summary>
    [Fact]
    public void A_role_that_already_holds_its_defaults_is_not_touched()
    {
        var role = new Role
        {
            Id = SystemRoles.AdminRoleId,
            Name = "Admin",
            SystemCapabilities = SystemCapabilities.DefaultsFor("Admin").ToList(),
        };

        DataSeeder.ApplyCapabilityDefaults(role).Should().BeFalse();
    }

    /// <summary>
    /// The cost of the rule above, asserted rather than left to be discovered.
    /// </summary>
    /// <remarks>
    /// A default an operator has deliberately removed from a seeded system role comes back on the
    /// next seed. There is nothing recording that the removal was deliberate, so the alternative is
    /// the bug above: never adding anything to a role that holds something. Removing a capability
    /// from a seeded role for good means not running the seeder. A role of your own is unaffected,
    /// because the defaults are keyed on the names the seeder creates.
    /// </remarks>
    [Fact]
    public void A_default_an_operator_removed_comes_back()
    {
        var narrowed = SystemCapabilities.DefaultsFor("Admin")
            .Where(c => c != SystemCapabilities.ManageApiKeys)
            .ToList();
        narrowed.Should().NotBeEmpty("the role has to keep something, or this is the empty-list case");

        var role = new Role { Id = SystemRoles.AdminRoleId, Name = "Admin", SystemCapabilities = narrowed };

        DataSeeder.ApplyCapabilityDefaults(role).Should().BeTrue();

        role.SystemCapabilities.Should().Contain(SystemCapabilities.ManageApiKeys,
            "this is the accepted cost of making later capabilities arrive, and it is documented");
    }

    [Fact]
    public void A_role_someone_created_gets_nothing_from_its_name()
    {
        var role = new Role { Id = Guid.NewGuid(), Name = "Admin " + Guid.NewGuid() };

        DataSeeder.ApplyCapabilityDefaults(role).Should().BeFalse();

        role.SystemCapabilities.Should().BeEmpty();
    }
}

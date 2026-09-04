using FluentAssertions;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Models;

/// <summary>
/// The capability vocabulary and the satisfies rule, which is what every capability gate reduces to.
/// </summary>
public class SystemCapabilitiesTests
{
    [Fact]
    public void A_role_holding_nothing_satisfies_nothing()
    {
        SystemCapabilities.Satisfies([], SystemCapabilities.ManageRoles).Should().BeFalse();
    }

    [Fact]
    public void A_role_satisfies_the_capability_it_holds()
    {
        SystemCapabilities.Satisfies([SystemCapabilities.ManageRoles], SystemCapabilities.ManageRoles)
            .Should().BeTrue();
    }

    [Fact]
    public void A_capability_does_not_satisfy_a_different_one()
    {
        SystemCapabilities.Satisfies([SystemCapabilities.ManageTenants], SystemCapabilities.ManageRoles)
            .Should().BeFalse();
    }

    /// <summary>
    /// Capabilities arrive from a stored role document, so casing is whatever an operator or an
    /// import happened to write.
    /// </summary>
    [Fact]
    public void Matching_ignores_case()
    {
        SystemCapabilities.Satisfies(["Manage_Roles"], SystemCapabilities.ManageRoles).Should().BeTrue();
    }

    [Fact]
    public void The_wildcard_satisfies_every_capability()
    {
        var everything = SystemCapabilities.Known.Where(c => c != SystemCapabilities.All).ToList();
        string[] wildcard = [SystemCapabilities.All];

        everything.Should().NotBeEmpty("the wildcard assertion below has nothing to run on otherwise");
        everything.Should().OnlyContain(c => SystemCapabilities.Satisfies(wildcard, c));
    }

    [Fact]
    public void An_unknown_capability_is_not_known()
    {
        SystemCapabilities.IsKnown("manage_the_moon").Should().BeFalse();
        SystemCapabilities.IsKnown("  ").Should().BeFalse();
    }

    [Fact]
    public void SuperAdmin_starts_with_everything()
    {
        SystemCapabilities.DefaultsFor("SuperAdmin").Should().Equal(SystemCapabilities.All);
    }

    /// <summary>
    /// The upgrade contract: Admin gets exactly the surfaces the old <c>Roles(...)</c> gates already
    /// let it reach, and none of the ones they did not. Roles, tenants, the user list and the
    /// password reset were <c>Roles("SuperAdmin")</c>, so an Admin picking them up here would be
    /// this change handing out access rather than preserving it. The list is asserted exactly, so
    /// each area migrated under #443 has to say here what Admin gains, if anything.
    /// </summary>
    [Fact]
    public void Admin_starts_with_what_Admin_could_already_reach_and_no_more()
    {
        var admin = SystemCapabilities.DefaultsFor("Admin");

        admin.Should().NotBeEmpty();
        admin.Should().BeEquivalentTo(new[]
        {
            SystemCapabilities.ManageTenantMembers,
            SystemCapabilities.ManageUserMembership,
            SystemCapabilities.ManageUserGroups,
            SystemCapabilities.ManageApiKeys,
            SystemCapabilities.ViewAuditLog,
            // Settings: GET and POST /api/settings and GET /api/settings/email were
            // Roles("SuperAdmin", "Admin"), so Admin reached them already.
            SystemCapabilities.ManageSettings,
            // Content types: all five routes were Roles("SuperAdmin", "Admin"), so Admin reached
            // both halves already and this migration narrows nothing.
            SystemCapabilities.ManageContentTypes,
            SystemCapabilities.ManagePublicDelivery,
            // Monitoring: /api/monitoring/health, /k8s and /metrics were Roles("Admin", "SuperAdmin").
            SystemCapabilities.ViewMonitoring,
            // Redirects: the list, the create, the delete and the import were all
            // Roles("SuperAdmin", "Admin") through RedirectGate.
            SystemCapabilities.ManageRedirects,
            // Queries: all five routes, preview included, were Roles("SuperAdmin", "Admin").
            SystemCapabilities.ManageQueries,
            // Requests: all five routes, dry run included, were Roles("SuperAdmin", "Admin").
            SystemCapabilities.ManageRequests,
            // Connectors: all six routes were Roles("SuperAdmin", "Admin"), so Admin holds both
            // halves of the read/write split and nothing is narrowed by it.
            SystemCapabilities.ViewConnectors,
            SystemCapabilities.ManageConnectors,
            // Workflows: authoring, the actions and variables lists, validate and dry-run were
            // Roles("SuperAdmin", "Admin").
            SystemCapabilities.ManageWorkflows,
            // Workflow runs: the two reads and the debug log were Roles("SuperAdmin", "Admin"), and
            // so was the retry, so Admin keeps both sides of that split too.
            SystemCapabilities.ViewWorkflowRuns,
            SystemCapabilities.RetryWorkflowActions,
            // Rollback: POST /api/contents/{id}/rollback/{versionId} was Roles("SuperAdmin", "Admin").
            SystemCapabilities.RollbackContent,
        });
        admin.Should().NotContain(SystemCapabilities.ManageRoles);
        admin.Should().NotContain(SystemCapabilities.ManageTenants);
        admin.Should().NotContain(SystemCapabilities.ManageUsers,
            "GET /api/users was SuperAdmin only, and handing it to Admin is exactly what #443 warns against");
        admin.Should().NotContain(SystemCapabilities.All);
        admin.Should().NotContain(SystemCapabilities.ManageEmailSettings,
            "PUT /api/settings/email was SuperAdmin only, and it redirects every password reset in "
          + "the deployment, so Admin picking it up here would be the widening #443 warns against");
        admin.Should().NotContain(SystemCapabilities.EraseContent,
            "DELETE /api/contents/{id}/erase was Roles(\"SuperAdmin\") and it destroys content and "
          + "its history irrecoverably, so Admin must not acquire it from the migration");
    }

    /// <summary>
    /// #443 is finished, so every name the vocabulary declares is one Admin either holds or is
    /// deliberately denied. This is what stops a name being added without that decision being made.
    /// </summary>
    [Fact]
    public void Every_capability_is_either_an_Admin_default_or_named_here_as_withheld()
    {
        string[] withheldFromAdmin =
        [
            SystemCapabilities.All,
            SystemCapabilities.ManageRoles,
            SystemCapabilities.ManageTenants,
            SystemCapabilities.ManageUsers,
            SystemCapabilities.ManageEmailSettings,
            SystemCapabilities.EraseContent,
        ];

        var admin = SystemCapabilities.DefaultsFor("Admin");

        SystemCapabilities.Known.Should().NotBeEmpty();
        SystemCapabilities.Known.Should().BeEquivalentTo(
            admin.Concat(withheldFromAdmin),
            "a capability that is neither granted to Admin nor listed as withheld is one nobody "
          + "decided about");
    }

    [Fact]
    public void A_role_the_seeder_does_not_create_starts_with_nothing()
    {
        SystemCapabilities.DefaultsFor("Editor").Should().BeEmpty();
        SystemCapabilities.DefaultsFor("Accountant").Should().BeEmpty();
        SystemCapabilities.DefaultsFor("HR").Should().BeEmpty();
        SystemCapabilities.DefaultsFor("User").Should().BeEmpty();
    }
}

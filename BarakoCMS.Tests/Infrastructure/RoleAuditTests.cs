using FluentAssertions;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Infrastructure;

/// <summary>
/// What a role looks like in an audit row. No database: these are the shapes a stored role can
/// take, including the ones only an old or hand-edited document has.
/// </summary>
public class RoleAuditTests
{
    [Fact]
    public void A_rule_with_conditions_is_recorded_as_conditional_and_its_values_are_left_out()
    {
        var lines = RoleAudit.Permissions(new[]
        {
            new ContentTypePermission
            {
                ContentTypeSlug = "invoice",
                Read = new PermissionRule { Enabled = true },
                Update = new PermissionRule
                {
                    Enabled = true,
                    Conditions = new Dictionary<string, object> { ["department"] = "finance-only-value" },
                },
            },
        });

        lines.Should().HaveCount(1);
        lines[0].Should().Be("invoice: read, update (conditional)");
        lines[0].Should().NotContain("finance-only-value");
    }

    [Fact]
    public void Transitions_are_named_and_a_type_with_nothing_enabled_says_none()
    {
        var lines = RoleAudit.Permissions(new[]
        {
            new ContentTypePermission
            {
                ContentTypeSlug = "invoice",
                Transitions = new Dictionary<string, PermissionRule>
                {
                    ["reject"] = new() { Enabled = false },
                    ["approve"] = new() { Enabled = true },
                },
            },
            new ContentTypePermission { ContentTypeSlug = "article" },
        });

        lines.Should().HaveCount(2);
        lines.Should().Equal("article: none", "invoice: transition:approve");
    }

    [Fact]
    public void A_stored_role_with_null_lists_and_null_rules_still_produces_a_row()
    {
        var role = new Role { Name = "Legacy", SystemCapabilities = null!, Permissions = null! };

        RoleAudit.Capabilities(role).Should().BeEmpty();
        RoleAudit.Permissions(role.Permissions).Should().BeEmpty();

        var lines = RoleAudit.Permissions(new[]
        {
            new ContentTypePermission
            {
                ContentTypeSlug = "page",
                Create = null!,
                Read = new PermissionRule { Enabled = true },
                Transitions = null!,
            },
        });

        lines.Should().HaveCount(1);
        lines[0].Should().Be("page: read");
    }

    [Fact]
    public void Added_compares_capability_names_without_regard_to_case()
    {
        var before = new[] { "view_audit_log", "Manage_Api_Keys" };
        var after = new[] { "manage_api_keys", "view_monitoring" };

        var added = RoleAudit.Added(before, after);
        var removed = RoleAudit.Added(after, before);

        added.Should().HaveCount(1);
        added.Should().Equal("view_monitoring");
        removed.Should().HaveCount(1);
        removed.Should().Equal("view_audit_log");
    }
}

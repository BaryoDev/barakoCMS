using System.Text.Json;
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
    private static Role RoleWith(params ContentTypePermission[] permissions) =>
        new() { Name = "Clerk", Permissions = permissions.ToList() };

    private static ContentTypePermission Invoice(string field, string op, object value) => new()
    {
        ContentTypeSlug = "invoice",
        Read = new PermissionRule { Enabled = true },
        Update = new PermissionRule
        {
            Enabled = true,
            Conditions = new Dictionary<string, object>
            {
                [field] = new Dictionary<string, object> { [op] = value },
            },
        },
    };

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void A_condition_is_recorded_as_its_field_and_operators_and_never_its_value()
    {
        var metadata = RoleAudit.Describe(RoleAudit.Of(RoleWith(Invoice("department", "_eq", "finance-only-value"))));

        var permissions = Json(metadata["permissions"]);
        permissions.GetProperty("count").GetInt32().Should().Be(1);
        var permission = permissions.GetProperty("items")[0];
        permission.GetProperty("contentType").GetString().Should().Be("invoice");
        permission.GetProperty("actions").EnumerateArray().Select(a => a.GetString())
            .Should().Equal("read", "update");

        var conditions = permission.GetProperty("conditions");
        conditions.GetProperty("count").GetInt32().Should().Be(1);
        var condition = conditions.GetProperty("items")[0];
        condition.GetProperty("rule").GetString().Should().Be("update");
        condition.GetProperty("field").GetString().Should().Be("department");
        condition.GetProperty("operators").EnumerateArray().Select(o => o.GetString()).Should().Equal("_eq");

        JsonSerializer.Serialize(metadata).Should().NotContain("finance-only-value");
    }

    [Fact]
    public void A_value_only_edit_is_flagged_though_the_recorded_shape_is_the_same()
    {
        var before = RoleAudit.Of(RoleWith(Invoice("department", "_in", new List<object> { "finance" })));
        var after = RoleAudit.Of(RoleWith(Invoice("department", "_in", new List<object> { "finance", "sales-marker" })));

        var metadata = RoleAudit.Changed(before, after);

        JsonSerializer.Serialize(metadata["permissionsBefore"])
            .Should().Be(JsonSerializer.Serialize(metadata["permissionsAfter"]), "the field and operator did not change");
        metadata["conditionsChanged"].Should().Be(true);
        Json(metadata["conditionsChangedIn"]).GetProperty("items").EnumerateArray().Select(i => i.GetString())
            .Should().Equal("invoice");
        JsonSerializer.Serialize(metadata).Should().NotContain("sales-marker");
    }

    [Fact]
    public void The_same_conditions_read_from_json_and_built_in_memory_are_not_a_change()
    {
        var stored = JsonSerializer.Deserialize<Dictionary<string, object>>(
            """{"status":{"_ne":"draft"},"department":{"_in":["finance",7]}}""")!;
        var built = new Dictionary<string, object>
        {
            ["department"] = new Dictionary<string, object> { ["_in"] = new List<object> { "finance", 7L } },
            ["status"] = new Dictionary<string, object> { ["_ne"] = "draft" },
        };

        ContentTypePermission With(Dictionary<string, object> conditions) => new()
        {
            ContentTypeSlug = "invoice",
            Update = new PermissionRule { Enabled = true, Conditions = conditions },
        };

        var metadata = RoleAudit.Changed(RoleAudit.Of(RoleWith(With(stored))), RoleAudit.Of(RoleWith(With(built))));

        metadata["conditionsChanged"].Should().Be(false);
        metadata.Should().NotContainKey("conditionsChangedIn");
    }

    [Fact]
    public void A_content_type_named_like_a_grant_stays_in_its_own_key()
    {
        var forged = "article: create, read, delete";
        var metadata = RoleAudit.Describe(RoleAudit.Of(RoleWith(new ContentTypePermission
        {
            ContentTypeSlug = forged,
            Read = new PermissionRule { Enabled = true },
        })));

        var permission = Json(metadata["permissions"]).GetProperty("items")[0];

        permission.GetProperty("contentType").GetString().Should().Be(forged);
        permission.GetProperty("actions").EnumerateArray().Select(a => a.GetString()).Should().Equal("read");
    }

    [Fact]
    public void Transitions_are_named_apart_from_actions_and_only_when_enabled()
    {
        var metadata = RoleAudit.Describe(RoleAudit.Of(RoleWith(new ContentTypePermission
        {
            ContentTypeSlug = "invoice",
            Transitions = new Dictionary<string, PermissionRule>
            {
                ["reject"] = new() { Enabled = false },
                ["approve"] = new() { Enabled = true },
            },
        })));

        var permission = Json(metadata["permissions"]).GetProperty("items")[0];

        permission.GetProperty("actions").GetArrayLength().Should().Be(0);
        permission.GetProperty("transitions").GetProperty("items").EnumerateArray().Select(t => t.GetString())
            .Should().Equal("approve");
    }

    [Fact]
    public void A_stored_role_with_null_lists_and_null_rules_still_produces_a_row()
    {
        var empty = RoleAudit.Describe(RoleAudit.Of(new Role { Name = "Legacy", SystemCapabilities = null!, Permissions = null! }));

        Json(empty["capabilities"]).GetProperty("count").GetInt32().Should().Be(0);
        Json(empty["permissions"]).GetProperty("count").GetInt32().Should().Be(0);

        var metadata = RoleAudit.Describe(RoleAudit.Of(RoleWith(new ContentTypePermission
        {
            ContentTypeSlug = "page",
            Create = null!,
            Read = new PermissionRule { Enabled = true },
            Transitions = null!,
        })));

        var permission = Json(metadata["permissions"]).GetProperty("items")[0];
        permission.GetProperty("actions").EnumerateArray().Select(a => a.GetString()).Should().Equal("read");
    }

    [Fact]
    public void Added_and_removed_ignore_case_and_are_left_out_when_nothing_differs()
    {
        var before = RoleAudit.Of(new Role { Name = "R", SystemCapabilities = ["view_audit_log", "Manage_Api_Keys"] });
        var after = RoleAudit.Of(new Role { Name = "R", SystemCapabilities = ["manage_api_keys", "view_monitoring"] });

        var changed = RoleAudit.Changed(before, after);
        var same = RoleAudit.Changed(before, before);

        Json(changed["capabilitiesAdded"]).GetProperty("items").EnumerateArray().Select(c => c.GetString())
            .Should().Equal("view_monitoring");
        Json(changed["capabilitiesRemoved"]).GetProperty("items").EnumerateArray().Select(c => c.GetString())
            .Should().Equal("view_audit_log");
        same.Should().NotContainKey("capabilitiesAdded");
        same.Should().NotContainKey("capabilitiesRemoved");
        same.Should().ContainKey("capabilitiesBefore");
    }

    [Fact]
    public void A_long_list_is_cut_at_the_cap_with_its_full_count_and_a_long_name_is_clipped()
    {
        var capabilities = Enumerable.Range(0, RoleAudit.MaxItems + 25).Select(i => $"capability_{i}").ToList();
        var metadata = RoleAudit.Describe(RoleAudit.Of(new Role
        {
            Name = new string('n', RoleAudit.MaxLength + 40),
            SystemCapabilities = capabilities,
        }));

        var capped = Json(metadata["capabilities"]);

        capped.GetProperty("items").GetArrayLength().Should().Be(RoleAudit.MaxItems);
        capped.GetProperty("count").GetInt32().Should().Be(RoleAudit.MaxItems + 25);
        capped.GetProperty("truncated").GetBoolean().Should().BeTrue();
        metadata["name"].ToString().Should().HaveLength(RoleAudit.MaxLength);
    }
}

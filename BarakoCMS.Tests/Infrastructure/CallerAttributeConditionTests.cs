using System.Text.Json;
using FluentAssertions;
using Xunit;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Infrastructure;

/// <summary>
/// <c>$CURRENT_USER.&lt;name&gt;</c> in a permission condition, against the caller's member profile.
/// </summary>
/// <remarks>
/// Most of these are about the direction of a failure. A caller with no value for an attribute has
/// to match nothing, and the operators that make that hard are the negative ones: "not equal to
/// nothing" reads naturally as true for every row.
/// </remarks>
public class CallerAttributeConditionTests
{
    private readonly ConditionEvaluator _evaluator = new();
    private readonly User _user = new() { Id = Guid.NewGuid() };

    private static Dictionary<string, object> Rule(string field, string op, object expected) => new()
    {
        [field] = new Dictionary<string, object> { [op] = expected },
    };

    private static Content Entry(object branch) => new()
    {
        Id = Guid.NewGuid(),
        ContentType = "order",
        Data = new Dictionary<string, object> { ["Branch"] = branch },
    };

    private static Dictionary<string, string> Profile(string name, string value) => new() { [name] = value };

    [Fact]
    public void An_entry_of_the_callers_branch_matches_and_one_of_another_branch_does_not()
    {
        var rule = Rule("Branch", "_eq", "$CURRENT_USER.branch");
        var profile = Profile("branch", "north");

        _evaluator.Evaluate(rule, Entry("north"), _user, profile).Should().BeTrue();
        _evaluator.Evaluate(rule, Entry("south"), _user, profile).Should().BeFalse();
    }

    [Fact]
    public void A_caller_with_no_membership_matches_nothing_whatever_the_operator()
    {
        foreach (var op in new[] { "_eq", "_ne", "_in", "_nin" })
        {
            _evaluator.Evaluate(Rule("Branch", op, "$CURRENT_USER.branch"), Entry("north"), _user, null)
                .Should().BeFalse("no profile means no value, and {0} must not read that as a grant", op);
        }
    }

    [Fact]
    public void A_profile_without_the_attribute_matches_nothing_whatever_the_operator()
    {
        var profile = Profile("ward", "north");

        foreach (var op in new[] { "_eq", "_ne", "_in", "_nin" })
        {
            _evaluator.Evaluate(Rule("Branch", op, "$CURRENT_USER.branch"), Entry("north"), _user, profile)
                .Should().BeFalse("the profile holds ward and not branch, so {0} has nothing to compare", op);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_attribute_with_an_empty_value_matches_nothing(string stored)
    {
        var profile = Profile("branch", stored);

        // An entry whose Branch is that same empty text is the input where a plain text comparison
        // would grant.
        _evaluator.Evaluate(Rule("Branch", "_eq", "$CURRENT_USER.branch"), Entry(stored), _user, profile)
            .Should().BeFalse("an empty attribute is no attribute");
        _evaluator.Evaluate(Rule("Branch", "_ne", "$CURRENT_USER.branch"), Entry("north"), _user, profile)
            .Should().BeFalse("and not equal to nothing is not a grant either");
    }

    [Fact]
    public void A_null_attribute_value_matches_nothing()
    {
        var profile = new Dictionary<string, string> { ["branch"] = null! };

        _evaluator.Evaluate(Rule("Branch", "_ne", "$CURRENT_USER.branch"), Entry("north"), _user, profile)
            .Should().BeFalse();
    }

    [Fact]
    public void An_attribute_name_is_case_sensitive()
    {
        var profile = Profile("Branch", "north");

        _evaluator.Evaluate(Rule("Branch", "_eq", "$CURRENT_USER.branch"), Entry("north"), _user, profile)
            .Should().BeFalse("the rule names branch and the profile holds Branch");
        _evaluator.Evaluate(Rule("Branch", "_eq", "$CURRENT_USER.Branch"), Entry("north"), _user, profile)
            .Should().BeTrue("the same name, in the same case, is the control");
    }

    [Theory]
    [InlineData("$CURRENT_USER.")]
    [InlineData("$CURRENT_USER.branch.name")]
    [InlineData("$CURRENT_USER. branch")]
    public void A_variable_naming_nothing_the_profile_holds_matches_nothing(string variable)
    {
        var profile = Profile("branch", "north");

        _evaluator.Evaluate(Rule("Branch", "_ne", variable), Entry("south"), _user, profile)
            .Should().BeFalse();
    }

    [Fact]
    public void Not_equal_selects_the_other_branches_only_for_a_caller_who_has_one()
    {
        var rule = Rule("Branch", "_ne", "$CURRENT_USER.branch");
        var profile = Profile("branch", "north");

        _evaluator.Evaluate(rule, Entry("south"), _user, profile).Should().BeTrue();
        _evaluator.Evaluate(rule, Entry("north"), _user, profile).Should().BeFalse();
    }

    [Fact]
    public void A_variable_under_in_or_not_in_denies_even_when_the_caller_has_the_attribute()
    {
        var profile = Profile("branch", "north");

        // _nin walks a scalar as its characters, so "south" is not in "north" and would be granted.
        _evaluator.Evaluate(Rule("Branch", "_nin", "$CURRENT_USER.branch"), Entry("south"), _user, profile)
            .Should().BeFalse();
        _evaluator.Evaluate(Rule("Branch", "_in", "$CURRENT_USER.branch"), Entry("n"), _user, profile)
            .Should().BeFalse();
    }

    [Fact]
    public void A_variable_inside_a_list_stays_text_as_CURRENT_USER_does()
    {
        var profile = Profile("branch", "north");
        var rule = Rule("Branch", "_in", new List<object> { "$CURRENT_USER.branch" });

        _evaluator.Evaluate(rule, Entry("north"), _user, profile).Should().BeFalse();
        _evaluator.Evaluate(rule, Entry("$CURRENT_USER.branch"), _user, profile).Should().BeTrue();
    }

    [Fact]
    public void A_field_of_another_type_is_compared_as_the_text_a_written_value_would_be()
    {
        var profile = Profile("level", "42");
        var rule = Rule("Branch", "_eq", "$CURRENT_USER.level");

        _evaluator.Evaluate(rule, Entry(42L), _user, profile)
            .Should().BeTrue("a rule written with the text 42 matches the number 42 too");
        _evaluator.Evaluate(rule, Entry(true), _user, profile).Should().BeFalse();
        _evaluator.Evaluate(rule, Entry(new List<object> { "42" }), _user, profile).Should().BeFalse();
        _evaluator.Evaluate(rule, Entry(JsonDocument.Parse("null").RootElement), _user, profile).Should().BeFalse();
    }

    [Fact]
    public void A_profile_value_is_not_read_as_a_variable_itself()
    {
        var profile = Profile("branch", "$CURRENT_USER");
        var rule = Rule("Branch", "_eq", "$CURRENT_USER.branch");

        _evaluator.Evaluate(rule, Entry(_user.Id.ToString()), _user, profile)
            .Should().BeFalse("the value is the text, not the caller's id");
        _evaluator.Evaluate(rule, Entry("$CURRENT_USER"), _user, profile).Should().BeTrue();
    }

    [Fact]
    public void A_rule_read_back_from_storage_resolves_the_variable_the_same_way()
    {
        // What a Role's Conditions look like after a Marten round trip: JsonElement all the way down.
        var rule = JsonSerializer.Deserialize<Dictionary<string, object>>(
            """{ "Branch": { "_eq": "$CURRENT_USER.branch" } }""")!;
        rule["Branch"].Should().BeOfType<JsonElement>("this test is about the stored shape");

        _evaluator.Evaluate(rule, Entry("north"), _user, Profile("branch", "north")).Should().BeTrue();
        _evaluator.Evaluate(rule, Entry("north"), _user, Profile("branch", "south")).Should().BeFalse();
        _evaluator.Evaluate(rule, Entry("north"), _user, null).Should().BeFalse();
    }

    [Fact]
    public void CURRENT_USER_alone_still_means_the_callers_id()
    {
        var rule = Rule("Branch", "_eq", "$CURRENT_USER");
        var profile = Profile("branch", "north");

        _evaluator.Evaluate(rule, Entry(_user.Id.ToString()), _user, profile).Should().BeTrue();
        _evaluator.Evaluate(rule, Entry("north"), _user, profile).Should().BeFalse();
        _evaluator.Evaluate(rule, Entry(_user.Id.ToString()), _user, null).Should().BeTrue(
            "the id needs no profile");
    }

    [Fact]
    public void The_older_overloads_still_read_the_variable_as_text()
    {
        var rule = Rule("Branch", "_eq", "$CURRENT_USER.branch");

        _evaluator.Evaluate(rule, Entry("$CURRENT_USER.branch"), _user).Should().BeTrue();
        _evaluator.Evaluate(rule, Entry("$CURRENT_USER.branch").Data, _user).Should().BeTrue();
        _evaluator.Evaluate(rule, Entry("north"), _user).Should().BeFalse();
    }

    private sealed class OlderEvaluator : IConditionEvaluator
    {
        public bool Evaluate(Dictionary<string, object> conditions, Dictionary<string, object> contentData, User user)
            => true;
    }

    [Fact]
    public void An_evaluator_written_before_variables_denies_a_rule_that_names_one()
    {
        IConditionEvaluator older = new OlderEvaluator();
        var profile = Profile("branch", "north");

        older.Evaluate(Rule("Branch", "_ne", "$CURRENT_USER.branch"), Entry("south"), _user, profile)
            .Should().BeFalse("it cannot resolve the variable, so it must not grant on it");

        var stored = JsonSerializer.Deserialize<Dictionary<string, object>>(
            """{ "Branch": { "_ne": "$CURRENT_USER.branch" } }""")!;
        older.Evaluate(stored, Entry("south"), _user, profile)
            .Should().BeFalse("the stored shape of the same rule is recognised too");

        older.Evaluate(Rule("Branch", "_eq", "north"), Entry("north"), _user, profile)
            .Should().BeTrue("a rule with no variable is answered as it always was");
    }

    [Fact]
    public void The_compiled_predicate_is_false_for_a_variable_the_caller_cannot_fill()
    {
        var rule = new PermissionRule { Enabled = true, Conditions = Rule("Branch", "_ne", "$CURRENT_USER.branch") };

        PermissionPredicateCompiler.Compile([rule], _user.Id, null).Sql.Should().Be("(FALSE)");
        PermissionPredicateCompiler.Compile([rule], _user.Id, Profile("ward", "north")).Sql.Should().Be("(FALSE)");
        PermissionPredicateCompiler.Compile([rule], _user.Id, Profile("branch", " ")).Sql.Should().Be("(FALSE)");
        PermissionPredicateCompiler.Compile([rule], _user.Id).Sql.Should().Be("(FALSE)");

        var inList = new PermissionRule { Enabled = true, Conditions = Rule("Branch", "_nin", "$CURRENT_USER.branch") };
        PermissionPredicateCompiler.Compile([inList], _user.Id, Profile("branch", "north")).Sql.Should().Be("(FALSE)");
    }

    [Fact]
    public void The_compiled_predicate_binds_the_attribute_as_a_parameter()
    {
        var rule = new PermissionRule { Enabled = true, Conditions = Rule("Branch", "_eq", "$CURRENT_USER.branch") };
        var hostile = "north' OR TRUE --";

        var predicate = PermissionPredicateCompiler.Compile([rule], _user.Id, Profile("branch", hostile));

        predicate.Compiled.Should().BeTrue();
        predicate.Sql.Should().NotContain("north", "a profile value never becomes SQL text");
        predicate.Parameters.Should().HaveCount(3);
        predicate.Parameters.Should().Equal("Branch", "Branch", hostile);
    }

    [Fact]
    public void A_profile_a_request_sends_is_refused_when_a_condition_could_not_read_it()
    {
        CallerAttributes.ProfileError(null).Should().BeNull("a request that sends no profile changes none");
        CallerAttributes.ProfileError(Profile("branch", "north")).Should().BeNull();
        CallerAttributes.ProfileError(Profile("person_record2", "x")).Should().BeNull();

        CallerAttributes.ProfileError(Profile("", "north")).Should().NotBeNull();
        CallerAttributes.ProfileError(Profile("2branch", "north")).Should().NotBeNull();
        CallerAttributes.ProfileError(Profile("branch.name", "north")).Should().NotBeNull();
        CallerAttributes.ProfileError(Profile("branch name", "north")).Should().NotBeNull();
        CallerAttributes.ProfileError(Profile(new string('a', CallerAttributes.MaxNameLength + 1), "north"))
            .Should().NotBeNull();
        CallerAttributes.ProfileError(Profile("branch", "")).Should().NotBeNull();
        CallerAttributes.ProfileError(Profile("branch", "  ")).Should().NotBeNull();
        CallerAttributes.ProfileError(new Dictionary<string, string> { ["branch"] = null! }).Should().NotBeNull();
        CallerAttributes.ProfileError(Profile("branch", new string('n', CallerAttributes.MaxValueLength + 1)))
            .Should().NotBeNull();
        CallerAttributes.ProfileError(new Dictionary<string, string> { ["branch"] = "north", ["Branch"] = "south" })
            .Should().NotBeNull("two names that differ only by case are a mistake waiting to be read");

        var tooMany = Enumerable.Range(0, CallerAttributes.MaxAttributes + 1)
            .ToDictionary(i => $"a{i}", _ => "x");
        CallerAttributes.ProfileError(tooMany).Should().NotBeNull();
    }
}

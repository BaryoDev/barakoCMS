using FluentAssertions;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// A rule an entry write could not apply is refused when the type is saved, so no type claims a
/// rule the API does not enforce.
/// </summary>
public class ContentTypeRuleValidationTests
{
    private static readonly ContentTypeValidatorService Validator = new();

    private static (bool IsValid, List<string> Errors) Save(string type, params (string Rule, object Value)[] rules) =>
        Validator.Validate("report", "Report",
        [
            new FieldDefinition
            {
                Name = "Subject",
                DisplayName = "Subject",
                Type = type,
                ValidationRules = rules.ToDictionary(r => r.Rule, r => r.Value),
            },
        ]);

    [Fact]
    public void The_known_rules_are_accepted()
    {
        Save("int", ("min", 0), ("max", 100)).IsValid.Should().BeTrue();
        Save("date", ("min", "2026-01-01"), ("max", "2026-12-31")).IsValid.Should().BeTrue();
        Save("string", ("minLength", 1), ("maxLength", 20), ("pattern", "^[A-Z]+$")).IsValid.Should().BeTrue();
        Save("string", ("requiredWhen", new Dictionary<string, object>
        {
            ["Kind"] = new Dictionary<string, object> { ["_eq"] = "Company" },
        })).IsValid.Should().BeTrue();
    }

    [Fact]
    public void An_unknown_rule_name_is_refused_naming_the_field_and_the_rule()
    {
        var (isValid, errors) = Save("string", ("matches", "^[A-Z]+$"));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Subject").And.Contain("'matches'").And.Contain("pattern");
    }

    [Fact]
    public void A_rule_named_regex_is_accepted_as_a_pattern()
    {
        Save("string", ("regex", "^[A-Z]+$")).IsValid.Should().BeTrue();

        var (isValid, errors) = Save("string", ("regex", "([A-Z"));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Subject").And.Contain("not a valid regular expression");
    }

    [Fact]
    public void A_field_with_both_regex_and_pattern_is_refused()
    {
        var (isValid, errors) = Save("string", ("regex", "^[A-Z]+$"), ("pattern", "^[A-Z]+$"));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Subject").And.Contain("'regex'").And.Contain("'pattern'");
    }

    [Fact]
    public void A_stored_field_with_an_unknown_rule_does_not_refuse_a_field_added_beside_it()
    {
        var stored = new FieldDefinition
        {
            Name = "Legacy",
            DisplayName = "Legacy",
            Type = "string",
            ValidationRules = new() { ["matches"] = "^[A-Z]+$" },
        };
        var added = new FieldDefinition { Name = "Grade", DisplayName = "Grade", Type = "int" };

        Validator.Validate("report", "Report", [stored, added], [stored]).IsValid.Should().BeTrue();

        added.ValidationRules = new() { ["matches"] = 1 };
        var (isValid, errors) = Validator.Validate("report", "Report", [stored, added], [stored]);

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Grade").And.Contain("'matches'");
    }

    [Fact]
    public void A_pattern_that_does_not_compile_is_refused()
    {
        var (isValid, errors) = Save("string", ("pattern", "([A-Z"));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Subject").And.Contain("not a valid regular expression");
    }

    [Fact]
    public void A_pattern_using_a_lookahead_or_a_backreference_is_refused()
    {
        var (isValid, errors) = Save("string", ("pattern", @"^(?=.*[0-9]).+$"));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Subject").And.Contain("lookahead").And.Contain("without backtracking");

        var (backreference, backreferenceErrors) = Save("string", ("pattern", @"^(a)\1$"));

        backreference.Should().BeFalse();
        backreferenceErrors.Should().HaveCount(1);
        backreferenceErrors[0].Should().Contain("backreference");
    }

    [Fact]
    public void A_condition_naming_a_document_property_or_the_current_user_is_refused()
    {
        var (isValid, errors) = Save("string", ("requiredWhen", new Dictionary<string, object>
        {
            ["$status"] = new Dictionary<string, object> { ["_eq"] = "Published" },
        }));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Subject").And.Contain("'requiredWhen'").And.Contain("$status");

        var (user, userErrors) = Save("string", ("requiredWhen", new Dictionary<string, object>
        {
            ["Owner"] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_USER" },
        }));

        user.Should().BeFalse();
        userErrors.Should().HaveCount(1);
        userErrors[0].Should().Contain("$CURRENT_USER");

        Save("string", ("requiredWhen", new Dictionary<string, object>
        {
            ["Owner"] = new Dictionary<string, object> { ["_in"] = new List<object> { "a", "$CURRENT_USER" } },
        })).IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_membership_condition_whose_bound_is_not_a_list_is_refused()
    {
        var (isValid, errors) = Save("string", ("requiredWhen", Condition("_in", "COMPETE")));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Subject").And.Contain("'_in'").And.Contain("not a list");

        Save("string", ("requiredWhen", Condition("_nin", "COMPETE"))).IsValid.Should().BeFalse();
        Save("string", ("requiredWhen", Condition("_in", new List<object> { "COMPETE" }))).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_stored_rule_a_save_would_refuse_is_classified_as_skipped()
    {
        var twice = new FieldDefinition
        {
            Name = "Grade",
            Type = "int",
            ValidationRules = new() { ["min"] = 50, ["MIN"] = 0, ["max"] = 100 },
        };

        var (applied, skipped) = barakoCMS.Core.Validation.FieldRules.Classify(twice);

        applied.Should().HaveCount(1);
        applied.Should().BeEquivalentTo(new[] { "max" });
        skipped.Should().HaveCount(2);
        skipped.Should().BeEquivalentTo(new[] { "min", "MIN" });

        var scalar = new FieldDefinition
        {
            Name = "ShirtSize",
            Type = "string",
            ValidationRules = new() { ["requiredWhen"] = Condition("_in", "COMPETE"), ["maxLength"] = 4 },
        };

        var (scalarApplied, scalarSkipped) = barakoCMS.Core.Validation.FieldRules.Classify(scalar);

        scalarApplied.Should().HaveCount(1);
        scalarApplied.Should().BeEquivalentTo(new[] { "maxLength" });
        scalarSkipped.Should().HaveCount(1);
        scalarSkipped.Should().BeEquivalentTo(new[] { "requiredWhen" });
    }

    [Fact]
    public void A_rule_on_a_field_type_it_does_not_apply_to_is_refused()
    {
        var (isValid, errors) = Save("bool", ("max", 1));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Subject").And.Contain("'max'");

        Save("int", ("maxLength", 5)).IsValid.Should().BeFalse();
        Save("int", ("pattern", "^1")).IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_bound_of_the_wrong_kind_is_refused()
    {
        Save("int", ("max", "a lot")).IsValid.Should().BeFalse();
        Save("date", ("min", "soon")).IsValid.Should().BeFalse();
        Save("string", ("maxLength", -1)).IsValid.Should().BeFalse();
        Save("string", ("maxLength", 2.5m)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_min_above_its_max_is_refused()
    {
        var (isValid, errors) = Save("int", ("min", 10), ("max", 5));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Subject").And.Contain("'min'").And.Contain("'max'");
    }

    private static Dictionary<string, object> Condition(string op, object bound) =>
        new() { ["Age"] = new Dictionary<string, object> { [op] = bound } };

    [Fact]
    public void A_condition_comparing_against_a_number_or_a_date_is_accepted()
    {
        Save("string", ("requiredWhen", Condition("_lt", 18))).IsValid.Should().BeTrue();
        Save("string", ("requiredWhen", Condition("_lte", 18))).IsValid.Should().BeTrue();
        Save("string", ("requiredWhen", Condition("_gt", 17.5m))).IsValid.Should().BeTrue();
        Save("string", ("requiredWhen", Condition("_gte", "2026-01-01"))).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_condition_comparing_against_a_value_that_is_not_a_number_or_a_date_is_refused()
    {
        var (isValid, errors) = Save("string", ("requiredWhen", Condition("_lt", "adult")));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Subject").And.Contain("'requiredWhen'").And.Contain("'_lt'");

        Save("string", ("requiredWhen", Condition("_gte", true))).IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_condition_with_an_unknown_comparison_is_refused()
    {
        var (isValid, errors) = Save("string", ("requiredWhen", new Dictionary<string, object>
        {
            ["Kind"] = new Dictionary<string, object> { ["_like"] = "Comp" },
        }));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Subject").And.Contain("'requiredWhen'").And.Contain("'_like'");

        Save("string", ("requiredWhen", "Kind")).IsValid.Should().BeFalse();
        Save("string", ("requiredWhen", new Dictionary<string, object>())).IsValid.Should().BeFalse();
    }
}

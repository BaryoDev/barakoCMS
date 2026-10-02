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
        var (isValid, errors) = Save("string", ("regex", "^[A-Z]+$"));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Subject").And.Contain("'regex'").And.Contain("pattern");
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

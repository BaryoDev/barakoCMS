using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Marten;
using NSubstitute;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// A field's ValidationRules are applied to every entry write. None of these schemas has a slug or a
/// reference field, so the validator never reaches its session and no database is needed.
/// </summary>
public class ValidationRuleEnforcementTests
{
    private static readonly ContentValidatorService Validator = new(Substitute.For<IQuerySession>());

    private static FieldDefinition Field(string name, string type, params (string Rule, object Value)[] rules) =>
        new()
        {
            Name = name,
            DisplayName = name,
            Type = type,
            ValidationRules = rules.ToDictionary(r => r.Rule, r => r.Value),
        };

    private static Task<(bool IsValid, List<string> Errors)> WriteAsync(
        Dictionary<string, object> data, params FieldDefinition[] fields) =>
        Validator.ValidateFieldsAsync(
            new ContentTypeDefinition { Name = "report", DisplayName = "Report", Fields = fields.ToList() },
            "report",
            data,
            existing: null);

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task A_number_above_max_is_refused_naming_the_field_and_the_rule()
    {
        var (isValid, errors) = await WriteAsync(
            new() { ["Grade"] = 101 },
            Field("Grade", "int", ("max", 100)));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Grade").And.Contain("'max'").And.Contain("100");
    }

    [Fact]
    public async Task A_number_below_min_is_refused()
    {
        var (isValid, errors) = await WriteAsync(
            new() { ["Price"] = 0.5m },
            Field("Price", "decimal", ("min", 1)));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Price").And.Contain("'min'");
    }

    [Fact]
    public async Task A_number_on_either_bound_is_accepted()
    {
        var field = Field("Grade", "int", ("min", 0), ("max", 100));

        (await WriteAsync(new() { ["Grade"] = 0 }, field)).IsValid.Should().BeTrue();
        (await WriteAsync(new() { ["Grade"] = 100 }, field)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Rules_and_values_that_arrive_as_json_are_enforced_the_same_way()
    {
        var (isValid, errors) = await WriteAsync(
            new() { ["Grade"] = Json(101) },
            Field("Grade", "int", ("max", Json(100))));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("'max'");
    }

    [Fact]
    public async Task A_date_after_max_is_refused()
    {
        var field = Field("Due", "date", ("min", "2026-01-01"), ("max", "2026-12-31"));

        var (isValid, errors) = await WriteAsync(new() { ["Due"] = Json("2027-01-01") }, field);

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Due").And.Contain("'max'");

        (await WriteAsync(new() { ["Due"] = Json("2026-06-15") }, field)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_date_before_min_is_refused()
    {
        var (isValid, errors) = await WriteAsync(
            new() { ["Due"] = Json("2025-12-31") },
            Field("Due", "date", ("min", "2026-01-01")));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("'min'");
    }

    [Fact]
    public async Task A_string_shorter_than_minLength_is_refused()
    {
        var (isValid, errors) = await WriteAsync(
            new() { ["Code"] = "ab" },
            Field("Code", "string", ("minLength", 3)));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Code").And.Contain("'minLength'");
    }

    [Fact]
    public async Task A_string_longer_than_maxLength_is_refused()
    {
        var (isValid, errors) = await WriteAsync(
            new() { ["Code"] = "abcdef" },
            Field("Code", "string", ("maxLength", 5)));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Code").And.Contain("'maxLength'");
    }

    [Fact]
    public async Task A_value_that_does_not_match_the_pattern_is_refused()
    {
        var field = Field("PatientNumber", "string", ("pattern", "^PT-[0-9]{6}$"));

        var (isValid, errors) = await WriteAsync(new() { ["PatientNumber"] = "PT-12" }, field);

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("PatientNumber").And.Contain("'pattern'");
        errors[0].Should().NotContain("[0-9]", "the pattern is a business rule and is not echoed to the writer");

        (await WriteAsync(new() { ["PatientNumber"] = "PT-123456" }, field)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_rule_named_regex_is_enforced_as_a_pattern()
    {
        var field = Field("PatientNumber", "string", ("regex", "^PT-[0-9]{6}$"));

        var (isValid, errors) = await WriteAsync(new() { ["PatientNumber"] = "PT-12" }, field);

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("PatientNumber").And.Contain("'pattern'");

        (await WriteAsync(new() { ["PatientNumber"] = "PT-123456" }, field)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_pattern_that_would_backtrack_answers_quickly_with_an_ordinary_mismatch()
    {
        // A backtracking engine has two to the thirty-ninth ways to split forty letters here and
        // tries them all before the character that fails the anchor, so it runs into the timeout.
        // Matching without backtracking reads the value once.
        var field = Field("Code", "string", ("pattern", @"^(\w+\s?)+$"));
        var clock = Stopwatch.StartNew();

        var (isValid, errors) = await WriteAsync(new() { ["Code"] = new string('a', 40) + "!" }, field);

        clock.Stop();
        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Code").And.Contain("does not match");
        errors[0].Should().NotEndWith(
            barakoCMS.Core.Validation.FieldRules.PatternTimedOut,
            "the match finished, it was not cut off");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task A_value_ending_in_a_line_break_is_refused_by_a_pattern()
    {
        var field = Field("Code", "string", ("pattern", "^[0-9]+$"));

        var (isValid, errors) = await WriteAsync(new() { ["Code"] = "123\n" }, field);

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Code").And.Contain("line break").And.Contain("'pattern'");

        (await WriteAsync(new() { ["Code"] = "123" }, field)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_number_beyond_the_decimal_range_is_above_any_max_and_below_any_min()
    {
        var price = Field("Price", "decimal", ("min", 0), ("max", 100));

        var (tooHigh, highErrors) = await WriteAsync(new() { ["Price"] = 1e30 }, price);

        tooHigh.Should().BeFalse();
        highErrors.Should().HaveCount(1);
        highErrors[0].Should().Contain("Price").And.Contain("'max'");

        var (tooLow, lowErrors) = await WriteAsync(new() { ["Price"] = -1e30 }, price);

        tooLow.Should().BeFalse();
        lowErrors.Should().HaveCount(1);
        lowErrors[0].Should().Contain("Price").And.Contain("'min'");
    }

    [Fact]
    public async Task A_cleared_optional_string_text_richtext_or_markdown_field_is_not_checked_against_its_rules()
    {
        foreach (var type in new[] { "string", "text", "richtext", "markdown" })
        {
            (await WriteAsync(new() { ["Code"] = "" }, Field("Code", type, ("minLength", 3))))
                .IsValid.Should().BeTrue("a cleared {0} field is not a value", type);
            (await WriteAsync(new() { ["Code"] = "   " }, Field("Code", type, ("pattern", "^[0-9]+$"))))
                .IsValid.Should().BeTrue("a cleared {0} field is not a value", type);
        }

        (await WriteAsync(new() { ["Code"] = Json("") }, Field("Code", "string", ("minLength", 3))))
            .IsValid.Should().BeTrue();

        (await WriteAsync(new() { ["Code"] = "ab" }, Field("Code", "string", ("minLength", 3))))
            .IsValid.Should().BeFalse("a value that is there is still checked");
    }

    [Fact]
    public async Task A_blank_email_url_slug_uuid_or_time_is_still_refused_by_its_type_check()
    {
        foreach (var type in new[] { "email", "url", "slug", "uuid", "time" })
        {
            var (isValid, errors) = await WriteAsync(new() { ["Code"] = "" }, Field("Code", type, ("minLength", 3)));

            isValid.Should().BeFalse("a blank {0} was refused before rules were applied and still is", type);
            errors.Should().HaveCount(1);
            errors[0].Should().Contain("expects type").And.NotContain("'minLength'");
        }
    }

    [Fact]
    public async Task A_stored_membership_condition_whose_bound_is_not_a_list_is_skipped()
    {
        var entryType = Field("EntryType", "string");

        (await WriteAsync(new() { ["EntryType"] = "C" }, entryType, RequiredWhen("ShirtSize", "EntryType", "_in", "COMPETE")))
            .IsValid.Should().BeTrue("a string is not a list of its characters");
        (await WriteAsync(new() { ["EntryType"] = "X" }, entryType, RequiredWhen("ShirtSize", "EntryType", "_nin", "COMPETE")))
            .IsValid.Should().BeTrue();

        var (isValid, errors) = await WriteAsync(
            new() { ["EntryType"] = "COMPETE" },
            entryType,
            RequiredWhen("ShirtSize", "EntryType", "_in", new List<object> { "COMPETE", "ELITE" }));

        isValid.Should().BeFalse("a list is still membership");
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("ShirtSize");
    }

    [Fact]
    public async Task A_stored_rule_under_two_spellings_is_skipped_whatever_order_they_were_stored_in()
    {
        (await WriteAsync(new() { ["Grade"] = 10 }, Field("Grade", "int", ("min", 50), ("MIN", 0))))
            .IsValid.Should().BeTrue();
        (await WriteAsync(new() { ["Grade"] = 10 }, Field("Grade", "int", ("MIN", 0), ("min", 50))))
            .IsValid.Should().BeTrue();

        var (isValid, errors) = await WriteAsync(
            new() { ["Grade"] = 200 }, Field("Grade", "int", ("min", 50), ("MIN", 0), ("max", 100)));

        isValid.Should().BeFalse("the rule stored once still applies");
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("'max'");
    }

    [Fact]
    public async Task A_required_text_field_left_blank_is_refused_as_required()
    {
        var code = Field("Code", "string", ("minLength", 3));
        code.IsRequired = true;

        var (isValid, errors) = await WriteAsync(new() { ["Code"] = "" }, code);

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Code").And.Contain("is required");

        var shirt = Field("ShirtSize", "string", ("minLength", 1), ("requiredWhen", new Dictionary<string, object>
        {
            ["EntryType"] = new Dictionary<string, object> { ["_eq"] = "COMPETE" },
        }));

        var (conditional, conditionalErrors) = await WriteAsync(
            new() { ["EntryType"] = "COMPETE", ["ShirtSize"] = "" }, Field("EntryType", "string"), shirt);

        conditional.Should().BeFalse();
        conditionalErrors.Should().HaveCount(1);
        conditionalErrors[0].Should().Contain("ShirtSize").And.Contain("'requiredWhen'");
    }

    [Fact]
    public async Task A_stored_min_above_its_max_does_not_refuse_every_write()
    {
        (await WriteAsync(new() { ["Grade"] = 7 }, Field("Grade", "int", ("min", 10), ("max", 5))))
            .IsValid.Should().BeTrue();
        (await WriteAsync(new() { ["Code"] = "abc" }, Field("Code", "string", ("minLength", 5), ("maxLength", 2))))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Two_keys_for_one_field_in_different_case_are_refused_in_either_order()
    {
        var kind = Field("Kind", "string");
        var taxNumber = RequiredWhen("TaxNumber", "Kind", "_eq", "Company");

        var (companyFirst, errors) = await WriteAsync(
            new() { ["kind"] = "Company", ["Kind"] = "Person" }, kind, taxNumber);

        companyFirst.Should().BeFalse();
        errors.Should().Contain(e => e.Contains("Kind") && e.Contains("more than once"));

        var (personFirst, personErrors) = await WriteAsync(
            new() { ["Kind"] = "Person", ["kind"] = "Company" }, kind, taxNumber);

        personFirst.Should().BeFalse("which key comes first must not decide what is checked");
        personErrors.Should().Contain(e => e.Contains("Kind") && e.Contains("more than once"));
    }

    [Fact]
    public async Task A_field_required_unless_a_value_is_required_when_the_other_field_is_left_out_or_null()
    {
        var kind = Field("Kind", "string");
        var taxNumber = RequiredWhen("TaxNumber", "Kind", "_ne", "Person");

        var (leftOut, leftOutErrors) = await WriteAsync(new() { ["Other"] = "x" }, kind, taxNumber);

        leftOut.Should().BeFalse();
        leftOutErrors.Should().HaveCount(1);
        leftOutErrors[0].Should().Contain("TaxNumber").And.Contain("'requiredWhen'");

        var (sentNull, nullErrors) = await WriteAsync(new() { ["Kind"] = null! }, kind, taxNumber);

        sentNull.Should().BeFalse();
        nullErrors.Should().HaveCount(1);
        nullErrors[0].Should().Contain("TaxNumber");

        (await WriteAsync(new() { ["Kind"] = "Person" }, kind, taxNumber)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_field_required_outside_a_list_is_required_when_the_other_field_is_left_out_or_null()
    {
        var kind = Field("Kind", "string");
        var taxNumber = RequiredWhen("TaxNumber", "Kind", "_nin", new List<object> { "Person", "Minor" });

        var (leftOut, leftOutErrors) = await WriteAsync(new() { ["Other"] = "x" }, kind, taxNumber);

        leftOut.Should().BeFalse();
        leftOutErrors.Should().HaveCount(1);
        leftOutErrors[0].Should().Contain("TaxNumber");

        var (sentNull, nullErrors) = await WriteAsync(new() { ["Kind"] = null! }, kind, taxNumber);

        sentNull.Should().BeFalse();
        nullErrors.Should().HaveCount(1);
        nullErrors[0].Should().Contain("TaxNumber");

        (await WriteAsync(new() { ["Kind"] = "Minor" }, kind, taxNumber)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_field_required_for_a_value_stays_optional_when_the_other_field_is_left_out()
    {
        var kind = Field("Kind", "string");

        (await WriteAsync(new() { ["Other"] = "x" }, kind, RequiredWhen("TaxNumber", "Kind", "_eq", "Company")))
            .IsValid.Should().BeTrue();
        (await WriteAsync(
                new() { ["Other"] = "x" },
                kind,
                RequiredWhen("TaxNumber", "Kind", "_in", new List<object> { "Company" })))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_field_is_required_only_while_its_condition_holds()
    {
        var kind = Field("Kind", "string");
        var taxNumber = Field(
            "TaxNumber",
            "string",
            ("requiredWhen", new Dictionary<string, object>
            {
                ["Kind"] = new Dictionary<string, object> { ["_eq"] = "Company" },
            }));

        var (isValid, errors) = await WriteAsync(new() { ["Kind"] = "Company" }, kind, taxNumber);

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("TaxNumber").And.Contain("'requiredWhen'");

        (await WriteAsync(new() { ["Kind"] = "Person" }, kind, taxNumber)).IsValid.Should().BeTrue();
        (await WriteAsync(new() { ["Kind"] = "Company", ["TaxNumber"] = "123" }, kind, taxNumber))
            .IsValid.Should().BeTrue();
    }

    private static FieldDefinition RequiredWhen(string name, string other, string op, object bound) =>
        Field(name, "string", ("requiredWhen", new Dictionary<string, object>
        {
            [other] = new Dictionary<string, object> { [op] = bound },
        }));

    [Fact]
    public async Task A_minor_without_a_guardian_is_refused_naming_the_guardian_field()
    {
        var (isValid, errors) = await WriteAsync(
            new() { ["Age"] = 16 },
            Field("Age", "int"),
            RequiredWhen("GuardianName", "Age", "_lt", 18));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("GuardianName").And.Contain("'requiredWhen'");
    }

    [Fact]
    public async Task An_adult_without_a_guardian_is_accepted()
    {
        var (isValid, errors) = await WriteAsync(
            new() { ["Age"] = 30 },
            Field("Age", "int"),
            RequiredWhen("GuardianName", "Age", "_lt", 18));

        errors.Should().BeEmpty();
        isValid.Should().BeTrue();
    }

    [Fact]
    public async Task An_age_exactly_on_the_bound_is_not_under_it()
    {
        var age = Field("Age", "int");

        (await WriteAsync(new() { ["Age"] = 18 }, age, RequiredWhen("GuardianName", "Age", "_lt", 18)))
            .IsValid.Should().BeTrue();

        var (isValid, errors) = await WriteAsync(
            new() { ["Age"] = 18 }, age, RequiredWhen("GuardianName", "Age", "_lte", 18));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("GuardianName");
    }

    [Fact]
    public async Task Greater_than_holds_above_the_bound_and_greater_or_equal_holds_on_it()
    {
        var size = Field("PartySize", "int");

        (await WriteAsync(new() { ["PartySize"] = 10 }, size, RequiredWhen("Organiser", "PartySize", "_gt", 10)))
            .IsValid.Should().BeTrue();
        (await WriteAsync(new() { ["PartySize"] = 11 }, size, RequiredWhen("Organiser", "PartySize", "_gt", 10)))
            .IsValid.Should().BeFalse();
        (await WriteAsync(new() { ["PartySize"] = 10 }, size, RequiredWhen("Organiser", "PartySize", "_gte", 10)))
            .IsValid.Should().BeFalse();
        (await WriteAsync(new() { ["PartySize"] = 9 }, size, RequiredWhen("Organiser", "PartySize", "_gte", 10)))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_comparison_stored_as_json_against_a_json_value_is_applied()
    {
        var guardian = Field("GuardianName", "string", ("requiredWhen", Json(new { Age = new { _lt = 18 } })));

        var (isValid, errors) = await WriteAsync(new() { ["Age"] = Json(16) }, Field("Age", "int"), guardian);

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("GuardianName");

        (await WriteAsync(new() { ["Age"] = Json(30) }, Field("Age", "int"), guardian)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_date_is_compared_as_a_date()
    {
        var born = Field("Born", "date");
        var guardian = RequiredWhen("GuardianName", "Born", "_gt", "2008-10-02");

        (await WriteAsync(new() { ["Born"] = Json("2010-05-01") }, born, guardian)).IsValid.Should().BeFalse();
        (await WriteAsync(new() { ["Born"] = Json("1996-05-01") }, born, guardian)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_comparison_against_a_value_that_is_missing_or_not_a_number_does_not_hold()
    {
        var guardian = RequiredWhen("GuardianName", "Level", "_lt", 18);

        (await WriteAsync(new() { ["Other"] = "x" }, Field("Level", "string"), Field("Other", "string"), guardian))
            .IsValid.Should().BeTrue();
        (await WriteAsync(new() { ["Level"] = "junior" }, Field("Level", "string"), guardian))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_compete_entry_without_a_shirt_size_is_refused_and_a_fun_entry_is_accepted()
    {
        var entryType = Field("EntryType", "string");
        var shirt = RequiredWhen("ShirtSize", "EntryType", "_eq", "COMPETE");

        var (isValid, errors) = await WriteAsync(new() { ["EntryType"] = "COMPETE" }, entryType, shirt);

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("ShirtSize").And.Contain("'requiredWhen'");

        (await WriteAsync(new() { ["EntryType"] = "FUN" }, entryType, shirt)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_condition_stored_as_json_and_a_key_in_another_case_still_make_the_field_required()
    {
        var kind = Field("Kind", "string");
        var taxNumber = Field("TaxNumber", "string", ("requiredWhen", Json(new { Kind = new { _eq = "Company" } })));

        var (isValid, errors) = await WriteAsync(new() { ["kind"] = Json("Company") }, kind, taxNumber);

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("'requiredWhen'");
    }

    [Fact]
    public async Task A_stored_rule_a_save_would_refuse_today_does_not_fail_the_write()
    {
        var (isValid, errors) = await WriteAsync(
            new() { ["Code"] = "abc" },
            Field("Code", "string", ("format", "^[0-9]+$"), ("pattern", "(")));

        errors.Should().BeEmpty();
        isValid.Should().BeTrue();

        (await WriteAsync(new() { ["Code"] = "b" }, Field("Code", "string", ("pattern", "^(?=a)a$"))))
            .IsValid.Should().BeTrue("a stored pattern that cannot be built without backtracking is skipped");
    }
}

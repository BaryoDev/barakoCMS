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
    public async Task A_pattern_that_runs_past_its_timeout_fails_the_write()
    {
        // Each run of word characters can end at any position and the optional space never matches,
        // so against forty letters and a character that fails the anchor the engine has two to the
        // thirty-ninth ways to split the input and tries them all.
        var field = Field("Code", "string", ("pattern", @"^(\w+\s?)+$"));
        var clock = Stopwatch.StartNew();

        var (isValid, errors) = await WriteAsync(new() { ["Code"] = new string('a', 40) + "!" }, field);

        clock.Stop();
        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Code").And.EndWith(
            barakoCMS.Core.Validation.FieldRules.PatternTimedOut,
            "a plain mismatch is a different message, and this has to be the cut-off one");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "the match is cut off, not waited for");
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
    }
}

using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

public class ArrayHasConditionTests
{
    private readonly IConditionEvaluator _eval = new ConditionEvaluator();

    private static Dictionary<string, object> Has(object expected) =>
        new() { ["VisibleToRoles"] = new Dictionary<string, object> { ["_has"] = expected } };

    private bool Passes(object? field, object expected, bool present = true)
    {
        var data = new Dictionary<string, object>();
        if (present && field is not null) data["VisibleToRoles"] = field;
        return _eval.Evaluate(Has(expected), data, new User { Id = Guid.NewGuid() });
    }

    [Fact] public void An_array_that_lists_the_value_passes() =>
        Passes(new List<object> { "Member", "Treasurer" }, "Treasurer").Should().BeTrue();

    [Fact] public void An_array_that_does_not_list_the_value_fails() =>
        Passes(new List<object> { "Member" }, "Treasurer").Should().BeFalse();

    [Fact] public void An_empty_array_fails() =>
        Passes(new List<object>(), "Member").Should().BeFalse();

    [Fact] public void A_missing_field_fails() =>
        Passes(null, "Member", present: false).Should().BeFalse();

    [Fact] public void A_string_field_is_not_an_array_and_never_matches_by_substring() =>
        Passes("Member,Treasurer", "Member").Should().BeFalse();

    [Fact] public void The_comparison_is_case_sensitive() =>
        Passes(new List<object> { "member" }, "Member").Should().BeFalse();

    [Fact] public void A_json_array_read_back_from_storage_passes()
    {
        var json = System.Text.Json.JsonDocument.Parse("[\"Auditor\",\"Member\"]").RootElement;
        Passes(json, "Member").Should().BeTrue();
    }

    [Fact] public void A_variable_as_the_expected_value_denies() =>
        Passes(new List<object> { "$CURRENT_USER" }, "$CURRENT_USER").Should().BeFalse();

    [Fact] public void A_non_string_expected_value_denies() =>
        Passes(new List<object> { 1 }, 1).Should().BeFalse();
}

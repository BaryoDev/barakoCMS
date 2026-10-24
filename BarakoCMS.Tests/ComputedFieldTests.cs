using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// Computed fields: what a type may declare, the value the server works out, and that a caller
/// cannot set it.
/// </summary>
[Collection("Integration2")]
public class ComputedFieldTests
{
    private static readonly ContentTypeValidatorService Validator = new();
    private readonly IntegrationTestFixture _fixture;

    public ComputedFieldTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FieldDefinition Plain(string name, string type) =>
        new() { Name = name, DisplayName = name, Type = type };

    private static FieldDefinition Computed(string name, string type, string fn, int? round, params string[] args) =>
        new() { Name = name, DisplayName = name, Type = type, Computed = new ComputedDefinition { Fn = fn, Args = args.ToList(), Round = round } };

    private static FieldDefinition[] TimeEntry() =>
    [
        Plain("TimeIn", "datetime"),
        Plain("TimeOut", "datetime"),
        Computed("Hours", "decimal", "hours", 2, "TimeIn", "TimeOut"),
    ];

    private static (bool IsValid, List<string> Errors) Save(params FieldDefinition[] fields) =>
        Validator.Validate("timeEntry", "Time entry", fields.ToList());

    private static Dictionary<string, object> Data(params (string Key, object Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void Hours_is_worked_out_from_time_in_and_time_out_and_rounded()
    {
        var data = Data(("TimeIn", "2026-10-19T08:00:00Z"), ("TimeOut", "2026-10-19T16:20:00Z"));

        ComputedFields.Apply(TimeEntry(), data);

        data.Should().ContainKey("Hours");
        data["Hours"].Should().Be(8.33m);
    }

    [Fact]
    public void Hours_is_left_out_while_time_out_is_not_set()
    {
        var data = Data(("TimeIn", "2026-10-19T08:00:00Z"));

        ComputedFields.Apply(TimeEntry(), data);

        data.Should().NotContainKey("Hours");
    }

    [Fact]
    public void A_value_sent_for_a_computed_field_is_replaced_by_the_computed_one()
    {
        var data = Data(("TimeIn", "2026-10-19T08:00:00Z"), ("TimeOut", "2026-10-19T10:00:00Z"), ("Hours", 99));

        ComputedFields.Apply(TimeEntry(), data);

        data["Hours"].Should().Be(2m);
    }

    [Theory]
    [InlineData("add", 7.5, 2.5, 10.0)]
    [InlineData("subtract", 7.5, 2.5, 5.0)]
    [InlineData("multiply", 4.0, 2.5, 10.0)]
    [InlineData("divide", 10.0, 4.0, 2.5)]
    [InlineData("min", 7.5, 2.5, 2.5)]
    [InlineData("max", 7.5, 2.5, 7.5)]
    public void Each_numeric_function_gives_its_result(string fn, double a, double b, double expected)
    {
        var field = Computed("Total", "decimal", fn, null, "A", "B");
        var data = Data(("A", (decimal)a), ("B", (decimal)b));

        ComputedFields.Compute(field, data).Should().Be((decimal)expected);
    }

    [Fact]
    public void Dividing_by_zero_gives_no_value_and_does_not_throw()
    {
        var field = Computed("Share", "decimal", "divide", null, "A", "B");

        ComputedFields.Compute(field, Data(("A", 1m), ("B", 0m))).Should().BeNull();
    }

    [Fact]
    public void An_int_result_too_large_for_a_long_gives_no_value_and_does_not_throw()
    {
        var fields = new[]
        {
            Plain("A", "int"),
            Plain("B", "int"),
            Plain("C", "int"),
            Computed("Product", "int", "multiply", null, "A", "B", "C"),
        };
        var data = Data(("A", 2000000000L), ("B", 2000000000L), ("C", 2000000000L));

        var act = () => ComputedFields.Apply(fields, data);

        act.Should().NotThrow();
        data.Should().NotContainKey("Product");
        data.Should().HaveCount(3);
    }

    [Fact]
    public void Days_duration_concat_coalesce_and_round_give_their_results()
    {
        var days = Computed("Days", "decimal", "days", null, "Start", "End");
        var minutes = Computed("Minutes", "int", "duration", null, "Start", "End");
        var data = Data(("Start", "2026-10-01"), ("End", "2026-10-04"), ("First", "Ana"), ("Last", "Reyes"), ("Raw", 2.345m));

        ComputedFields.Compute(days, data).Should().Be(3m);
        ComputedFields.Compute(minutes, data).Should().Be(4320L);
        ComputedFields.Compute(Computed("Full", "string", "concat", null, "First", "Last"), data).Should().Be("AnaReyes");
        ComputedFields.Compute(Computed("Name", "string", "coalesce", null, "Nick", "First"), data).Should().Be("Ana");
        ComputedFields.Compute(Computed("Near", "decimal", "round", 2, "Raw"), data).Should().Be(2.35m);
    }

    [Fact]
    public void A_computed_field_reading_another_is_worked_out_after_it()
    {
        var fields = new[]
        {
            Computed("Total", "decimal", "multiply", null, "Subtotal", "Rate"),
            Computed("Subtotal", "decimal", "add", null, "A", "B"),
            Plain("A", "decimal"),
            Plain("B", "decimal"),
            Plain("Rate", "decimal"),
        };
        var data = Data(("A", 1m), ("B", 2m), ("Rate", 2m));

        ComputedFields.Apply(fields, data);

        data["Subtotal"].Should().Be(3m);
        data["Total"].Should().Be(6m);
    }

    [Fact]
    public void A_valid_computed_definition_is_accepted()
    {
        var (isValid, errors) = Save(TimeEntry());

        isValid.Should().BeTrue(string.Join("; ", errors));
    }

    [Fact]
    public void A_cycle_between_two_computed_fields_is_refused_when_the_type_is_saved()
    {
        var (isValid, errors) = Save(
            Computed("A", "decimal", "add", null, "B", "1"),
            Computed("B", "decimal", "add", null, "A", "1"));

        isValid.Should().BeFalse();
        errors.Should().Contain(e => e.Contains("circle") && e.Contains("A") && e.Contains("B"));
    }

    [Fact]
    public void A_field_computed_from_itself_is_refused()
    {
        var (isValid, errors) = Save(Computed("A", "decimal", "add", null, "A", "1"));

        isValid.Should().BeFalse();
        errors.Should().Contain(e => e.Contains("'A' computes from itself"));
    }

    [Fact]
    public void An_input_the_type_does_not_declare_is_refused()
    {
        var (isValid, errors) = Save(Plain("TimeIn", "datetime"), Computed("Hours", "decimal", "hours", 2, "TimeIn", "TimeOut"));

        isValid.Should().BeFalse();
        errors.Should().Contain(e => e.Contains("'TimeOut'") && e.Contains("does not declare"));
    }

    [Theory]
    [InlineData("hours", "decimal", "string")]
    [InlineData("add", "decimal", "datetime")]
    [InlineData("add", "string", "decimal")]
    [InlineData("concat", "decimal", "string")]
    [InlineData("nope", "decimal", "decimal")]
    public void A_function_the_types_do_not_fit_is_refused(string fn, string resultType, string inputType)
    {
        var (isValid, errors) = Save(
            Plain("X", inputType), Plain("Y", inputType), Computed("Out", resultType, fn, null, "X", "Y"));

        isValid.Should().BeFalse();
        errors.Should().NotBeEmpty();
        errors.Should().OnlyContain(e => e.Contains("Out"));
    }

    [Fact]
    public void A_computed_field_more_widely_readable_than_its_input_is_refused()
    {
        var salary = Plain("Salary", "decimal");
        salary.Sensitivity = SensitivityLevel.Sensitive;
        var bonus = Plain("Bonus", "decimal");
        bonus.Sensitivity = SensitivityLevel.Sensitive;

        var (isValid, errors) = Save(salary, bonus, Computed("Pay", "decimal", "add", null, "Salary", "Bonus"));

        isValid.Should().BeFalse();
        errors.Should().Contain(e => e.Contains("'Pay'") && e.Contains("more widely readable"));
    }

    private async Task<(string Type, HttpClient Client)> StoreTimeEntryTypeAsync()
    {
        var type = await TokenFieldProbe.StoreTypeAsync(_fixture, "timeentry", true, TimeEntry());
        var client = TokenFieldProbe.ClientFor(_fixture, await TokenFieldProbe.AdminAsync(_fixture));
        return (type, client);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string type, Dictionary<string, object> data) =>
        client.PostAsJsonAsync(
            "/api/contents",
            new barakoCMS.Features.Content.Create.Request { ContentType = type, Data = data, Status = ContentStatus.Published },
            ApiJson.Options,
            Ct);

    [Fact]
    public async Task Hours_is_stored_when_the_entry_is_created_and_changes_when_time_out_is_edited()
    {
        var (type, client) = await StoreTimeEntryTypeAsync();

        var created = await PostAsync(client, type, Data(("TimeIn", "2026-10-19T08:00:00Z"), ("TimeOut", "2026-10-19T12:00:00Z")));
        var body = await created.Content.ReadAsStringAsync(Ct);
        created.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();

        (await TokenFieldProbe.StoredAsync(_fixture, id)).Data["Hours"].ToString().Should().Be("4");

        var edit = await TokenFieldProbe.UpdateAsync(
            _fixture, await TokenFieldProbe.AdminAsync(_fixture), id,
            Data(("TimeIn", "2026-10-19T08:00:00Z"), ("TimeOut", "2026-10-19T17:30:00Z")));
        edit.StatusCode.Should().Be(HttpStatusCode.OK, await edit.Content.ReadAsStringAsync(Ct));

        (await TokenFieldProbe.StoredAsync(_fixture, id)).Data["Hours"].ToString().Should().Be("9.5");
    }

    [Fact]
    public async Task Setting_hours_directly_is_a_400_naming_the_field()
    {
        var (type, client) = await StoreTimeEntryTypeAsync();

        var response = await PostAsync(client, type, Data(("TimeIn", "2026-10-19T08:00:00Z"), ("TimeOut", "2026-10-19T12:00:00Z"), ("Hours", 12)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("Hours").And.Contain("computed");
    }

    [Fact]
    public async Task Setting_hours_on_an_edit_is_a_400_and_sending_the_stored_value_back_is_not()
    {
        var (type, client) = await StoreTimeEntryTypeAsync();
        var created = await PostAsync(client, type, Data(("TimeIn", "2026-10-19T08:00:00Z"), ("TimeOut", "2026-10-19T12:00:00Z")));
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("id").GetGuid();
        var admin = await TokenFieldProbe.AdminAsync(_fixture);

        var forged = await TokenFieldProbe.UpdateAsync(
            _fixture, admin, id, Data(("TimeIn", "2026-10-19T08:00:00Z"), ("TimeOut", "2026-10-19T12:00:00Z"), ("Hours", 40)));
        var echoed = await TokenFieldProbe.UpdateAsync(
            _fixture, admin, id, Data(("TimeIn", "2026-10-19T08:00:00Z"), ("TimeOut", "2026-10-19T12:00:00Z"), ("Hours", 4)));

        forged.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await forged.Content.ReadAsStringAsync(Ct)).Should().Contain("Hours");
        echoed.StatusCode.Should().Be(HttpStatusCode.OK, await echoed.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Delivery_filters_and_sorts_by_the_stored_value()
    {
        var (type, client) = await StoreTimeEntryTypeAsync();
        foreach (var (hours, label) in new[] { (3, "short"), (9, "long"), (6, "middle") })
        {
            var response = await PostAsync(client, type, Data(
                ("TimeIn", "2026-10-19T08:00:00Z"), ("TimeOut", $"2026-10-19T{8 + hours:00}:00:00Z")));
            response.StatusCode.Should().Be(HttpStatusCode.OK, label);
        }

        async Task<List<string>> HoursAsync(string query)
        {
            var res = await _fixture.CreateClient().GetAsync($"/api/public/{type}?{query}", Ct);
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            var root = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct)).RootElement;
            return root.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("data").GetProperty("Hours").ToString()).ToList();
        }

        (await HoursAsync("sort=Hours")).Should().Equal("3", "6", "9");
        (await HoursAsync("sort=-Hours")).Should().Equal("9", "6", "3");
        (await HoursAsync("filter[Hours][gt]=4&sort=Hours")).Should().Equal("6", "9");
    }
}

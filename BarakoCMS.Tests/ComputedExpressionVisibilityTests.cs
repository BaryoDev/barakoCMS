using System.Net;
using System.Text.Json;
using FluentAssertions;
using barakoCMS.Features.Content.Types;
using barakoCMS.Models;
using Probe = BarakoCMS.Tests.TokenFieldProbe;

namespace BarakoCMS.Tests;

/// <summary>
/// A formula names the fields it reads, so it is shown only to a reader who may read every one.
/// </summary>
/// <remarks>
/// Types here are stored straight into the database, the way a type from before the save-time
/// audience check can be, because a public formula over a hidden field is refused when saved.
/// </remarks>
[Collection("Integration1")]
public class ComputedExpressionVisibilityTests
{
    private const string CostValue = "43.21";
    private const string MarginValue = "56.79";

    private readonly IntegrationTestFixture _fixture;

    public ComputedExpressionVisibilityTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FieldDefinition Number(string name, SensitivityLevel level = SensitivityLevel.Public) =>
        new() { Name = name, DisplayName = name, Type = "decimal", Sensitivity = level };

    private static FieldDefinition Formula(string name, string expression, SensitivityLevel level = SensitivityLevel.Public) => new()
    {
        Name = name, DisplayName = name, Type = "decimal", Sensitivity = level,
        Computed = new ComputedDefinition { Expression = expression },
    };

    // Unit: the rule the type shape applies.

    [Fact]
    public void A_formula_naming_a_hidden_field_is_left_out_whole()
    {
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Cost" };

        ReadableField.ReadableComputed(new ComputedDefinition { Expression = "Qty * Cost" }, hidden).Should().BeNull();
        ReadableField.ReadableComputed(new ComputedDefinition { Expression = "Qty * cost" }, hidden).Should().BeNull("names match without case");
        ReadableField.ReadableComputed(new ComputedDefinition { Expression = "round(data.Cost, 2)" }, hidden).Should().BeNull();
        ReadableField.ReadableComputed(new ComputedDefinition { Expression = "iif(Qty > 1, Price, Cost)" }, hidden).Should().BeNull("a branch counts as read");
    }

    [Fact]
    public void A_formula_naming_only_readable_fields_is_shown()
    {
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Cost" };
        var rule = new ComputedDefinition { Expression = "Qty * Price" };

        ReadableField.ReadableComputed(rule, hidden).Should().BeSameAs(rule);
        ReadableField.ReadableComputed(rule, new HashSet<string>()).Should().BeSameAs(rule);
    }

    [Fact]
    public void A_formula_that_does_not_parse_is_left_out_for_a_reader_missing_any_field()
    {
        var broken = new ComputedDefinition { Expression = "Qty * " };

        ReadableField.ReadableComputed(broken, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Cost" }).Should().BeNull();
        ReadableField.ReadableComputed(broken, new HashSet<string>()).Should().BeSameAs(broken, "a reader who misses nothing may read it");
    }

    [Fact]
    public void A_function_rule_is_left_out_by_its_arguments_as_before()
    {
        var rule = new ComputedDefinition { Fn = "add", Args = ["Qty", "Cost"] };

        ReadableField.ReadableComputed(rule, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Cost" }).Should().BeNull();
        ReadableField.ReadableComputed(rule, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Price" }).Should().BeSameAs(rule);
    }

    // Integration: what each kind of reader is served.

    private async Task<(string Type, Guid Id)> StoredOrderAsync()
    {
        var type = await Probe.StoreTypeAsync(
            _fixture, "viz", deliverable: true,
            Number("Qty"),
            Number("Price"),
            Number("Cost", SensitivityLevel.Hidden),
            Formula("Revenue", "Qty * Price"),
            Formula("Total", "Qty * Cost"),
            Formula("Margin", "Price - Cost", SensitivityLevel.Hidden));

        var admin = await Probe.AdminAsync(_fixture);
        var id = await Probe.CreateAsync(
            _fixture, admin, type,
            new() { ["Qty"] = 2, ["Price"] = 100, ["Cost"] = decimal.Parse(CostValue, System.Globalization.CultureInfo.InvariantCulture) },
            ContentStatus.Published);
        return (type, id);
    }

    private static JsonElement Field(JsonElement shape, string name) =>
        shape.GetProperty("fields").EnumerateArray().FirstOrDefault(f => f.GetProperty("name").GetString() == name);

    private static bool HasFormula(JsonElement field) =>
        field.ValueKind == JsonValueKind.Object
        && field.TryGetProperty("computed", out var computed)
        && computed.ValueKind == JsonValueKind.Object
        && computed.TryGetProperty("expression", out var expression)
        && expression.ValueKind == JsonValueKind.String;

    private async Task<JsonElement> ShapeAsync(string token, string type)
    {
        var res = await Probe.ClientFor(_fixture, token).GetAsync($"/api/contents/types/{type}", Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task A_reader_of_every_field_is_shown_every_formula()
    {
        var (type, _) = await StoredOrderAsync();

        var shape = await ShapeAsync(await Probe.AdminAsync(_fixture), type);

        HasFormula(Field(shape, "Revenue")).Should().BeTrue();
        HasFormula(Field(shape, "Total")).Should().BeTrue();
        HasFormula(Field(shape, "Margin")).Should().BeTrue();
    }

    [Fact]
    public async Task A_low_role_is_shown_no_formula_over_a_hidden_field_and_still_the_one_over_readable_fields()
    {
        var (type, _) = await StoredOrderAsync();
        var viewer = await Probe.UserAsync(_fixture, $"Viewer_{Guid.NewGuid():N}", type);

        var shape = await ShapeAsync(viewer, type);

        var names = shape.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToList();
        names.Should().Contain(["Qty", "Price", "Revenue", "Total"]);
        names.Should().NotContain(["Cost", "Margin"], "the fields are hidden from this reader");
        HasFormula(Field(shape, "Revenue")).Should().BeTrue("it reads only fields this reader may read");
        HasFormula(Field(shape, "Total")).Should().BeFalse("it reads Cost");
        shape.ToString().Should().NotContain("Cost").And.NotContain("Price - Cost");
    }

    [Fact]
    public async Task A_low_role_is_shown_no_value_of_a_hidden_formula_or_the_field_it_reads()
    {
        var (type, id) = await StoredOrderAsync();
        var viewer = await Probe.UserAsync(_fixture, $"Viewer_{Guid.NewGuid():N}", type);

        var res = await Probe.ClientFor(_fixture, viewer).GetAsync($"/api/contents/{id}", Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var data = JsonDocument.Parse(body).RootElement.GetProperty("data");
        data.TryGetProperty("Qty", out _).Should().BeTrue("the entry is readable, so the absences below are the field rule");
        data.TryGetProperty("Cost", out _).Should().BeFalse();
        data.TryGetProperty("Margin", out _).Should().BeFalse();
        body.Should().NotContain(CostValue).And.NotContain(MarginValue);
    }

    [Fact]
    public async Task The_stored_entry_still_holds_every_value_so_the_absence_above_is_not_a_missing_computation()
    {
        var (_, id) = await StoredOrderAsync();

        var stored = await Probe.StoredAsync(_fixture, id);

        stored.Data["Margin"].ToString().Should().Be(MarginValue);
        stored.Data["Revenue"].ToString().Should().Be("200");
    }

    [Fact]
    public async Task An_anonymous_reader_is_shown_no_hidden_formula_no_value_and_no_expression()
    {
        var (type, _) = await StoredOrderAsync();

        var res = await _fixture.CreateClient().GetAsync($"/api/public/{type}", Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var item = JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray().Single().GetProperty("data");
        item.TryGetProperty("Revenue", out var revenue).Should().BeTrue("the public formula is delivered");
        revenue.ToString().Should().Be("200");
        item.TryGetProperty("Cost", out _).Should().BeFalse();
        item.TryGetProperty("Margin", out _).Should().BeFalse();
        body.Should().NotContain(CostValue).And.NotContain(MarginValue).And.NotContain("expression");
    }

    [Fact]
    public async Task The_public_type_description_serves_no_formula_and_names_no_hidden_field()
    {
        var (type, _) = await StoredOrderAsync();

        var res = await _fixture.CreateClient().GetAsync($"/api/public/types/{type}/description", Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().NotContain("expression").And.NotContain("Qty * Cost").And.NotContain("Price - Cost").And.NotContain("Margin");
    }
}

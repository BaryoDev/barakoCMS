using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using BarakoCMS.Tests.Features.Tenants;

namespace BarakoCMS.Tests;

/// <summary>
/// What a type may declare as a formula: the checks at save, the rules a formula shares with the
/// function rules, and the backfill when a formula changes.
/// </summary>
[Collection("Integration1")]
public class ComputedExpressionSchemaTests
{
    private static readonly ContentTypeValidatorService Validator = new();
    private readonly IntegrationTestFixture _fixture;

    public ComputedExpressionSchemaTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FieldDefinition Plain(string name, string type) =>
        new() { Name = name, DisplayName = name, Type = type };

    private static FieldDefinition Formula(string name, string type, string expression, int? round = null) =>
        new() { Name = name, DisplayName = name, Type = type, Computed = new ComputedDefinition { Expression = expression, Round = round } };

    private static FieldDefinition Guarded(FieldDefinition f, SensitivityLevel level, params string[] roles)
    {
        f.Sensitivity = level;
        f.VisibleToRoles = roles.ToList();
        return f;
    }

    private static ContentTypeDefinition TypeOf(params FieldDefinition[] fields) =>
        new() { Name = "order", DisplayName = "Order", Fields = fields.ToList() };

    private static (bool IsValid, List<string> Errors) Save(params FieldDefinition[] fields) =>
        Validator.Validate("order", "Order", fields.ToList());

    private static FieldDefinition[] Order(string expression = "Qty * Cost") =>
    [
        Plain("Qty", "decimal"),
        Plain("Cost", "decimal"),
        Formula("Total", "decimal", expression, 2),
    ];

    [Fact]
    public void A_valid_formula_is_accepted()
    {
        var (isValid, errors) = Save(Order());

        errors.Should().BeEmpty();
        isValid.Should().BeTrue();
    }

    [Fact]
    public void A_rule_with_both_a_function_and_a_formula_is_refused()
    {
        var total = Formula("Total", "decimal", "Qty * Cost");
        total.Computed!.Fn = "add";
        total.Computed.Args = ["Qty", "Cost"];

        var (isValid, errors) = Save(Plain("Qty", "decimal"), Plain("Cost", "decimal"), total);

        isValid.Should().BeFalse();
        errors.Should().ContainSingle(e => e.Contains("'Total'") && e.Contains("one or the other"));
    }

    [Theory]
    [InlineData("Qty * Missing", "Total", "unknown-name")]
    [InlineData("Qty * today()", "Total", "clock")]
    [InlineData("Qty * year(now())", "Total", "clock")]
    [InlineData("Qty * 2 +", "Total", "syntax")]
    public void A_formula_the_checker_refuses_is_refused_with_its_rule_and_position(string expression, string field, string rule)
    {
        var (isValid, errors) = Save(Plain("Qty", "decimal"), Plain("Cost", "decimal"), Formula(field, "decimal", expression));

        isValid.Should().BeFalse();
        errors.Should().Contain(e => e.Contains($"'{field}'") && e.Contains("position"), rule);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("status")]
    [InlineData("createdAt")]
    [InlineData("updatedAt")]
    public void A_formula_reading_a_system_name_is_refused(string name)
    {
        var (isValid, errors) = Save(Plain("Qty", "decimal"), Formula("Label", "string", $"concat({name}, \"x\")"));

        isValid.Should().BeFalse();
        errors.Should().Contain(e => e.Contains("'Label'") && e.Contains($"'{name}'") && e.Contains("not part of the entry's data"));
    }

    [Theory]
    [InlineData("string", "Qty * 2")]
    [InlineData("decimal", "concat(Name, \"x\")")]
    [InlineData("bool", "Qty * 2")]
    [InlineData("date", "Qty * 2")]
    [InlineData("decimal", "null")]
    public void A_formula_whose_result_does_not_fit_the_field_type_is_refused(string fieldType, string expression)
    {
        var (isValid, errors) = Save(Plain("Qty", "decimal"), Plain("Name", "string"), Formula("Result", fieldType, expression));

        isValid.Should().BeFalse();
        errors.Should().Contain(e => e.Contains("'Result'"));
    }

    [Fact]
    public void A_round_on_a_formula_that_does_not_give_a_number_is_refused()
    {
        var (isValid, errors) = Save(Plain("Name", "string"), Formula("Label", "string", "upper(Name)", 2));

        isValid.Should().BeFalse();
        errors.Should().ContainSingle(e => e.Contains("'Label'") && e.Contains("takes no round"));
    }

    [Fact]
    public void A_formula_field_cannot_be_required_or_take_a_default()
    {
        var required = Formula("Total", "decimal", "Qty * 2");
        required.IsRequired = true;
        var defaulted = Formula("Other", "decimal", "Qty * 3");
        defaulted.DefaultValue = 4;

        var (isValid, errors) = Save(Plain("Qty", "decimal"), required, defaulted);

        isValid.Should().BeFalse();
        errors.Should().Contain(e => e.Contains("'Total'") && e.Contains("required"));
        errors.Should().Contain(e => e.Contains("'Other'") && e.Contains("default"));
    }

    [Fact]
    public void A_formula_reading_its_own_field_is_refused()
    {
        var (isValid, errors) = Save(Plain("Qty", "decimal"), Formula("Total", "decimal", "Qty + Total"));

        isValid.Should().BeFalse();
        errors.Should().Contain(e => e.Contains("'Total'") && e.Contains("itself"));
    }

    [Fact]
    public void A_cycle_through_formulas_is_refused()
    {
        var (isValid, errors) = Save(Formula("A", "decimal", "B + 1"), Formula("B", "decimal", "A + 1"));

        isValid.Should().BeFalse();
        errors.Should().Contain(e => e.Contains("circle"));
    }

    [Fact]
    public void A_cycle_between_a_function_rule_and_a_formula_is_refused()
    {
        var byFn = new FieldDefinition
        {
            Name = "A", DisplayName = "A", Type = "decimal",
            Computed = new ComputedDefinition { Fn = "add", Args = ["B", "B"] },
        };

        var (isValid, errors) = Save(byFn, Formula("B", "decimal", "A + 1"));

        isValid.Should().BeFalse();
        errors.Should().Contain(e => e.Contains("circle"));
    }

    [Fact]
    public void A_formula_reading_another_formula_is_accepted_in_either_order()
    {
        var (isValid, errors) = Save(Plain("Qty", "decimal"), Formula("Double", "decimal", "Total * 2"), Formula("Total", "decimal", "Qty + 1"));

        errors.Should().BeEmpty();
        isValid.Should().BeTrue();
        ComputedFields.InOrder([Formula("Double", "decimal", "Total * 2"), Formula("Total", "decimal", "Qty + 1")])
            .Select(f => f.Name).Should().Equal("Total", "Double");
    }

    [Fact]
    public void A_public_formula_over_a_hidden_input_is_refused_as_more_widely_readable()
    {
        var (isValid, errors) = Save(
            Guarded(Plain("Cost", "decimal"), SensitivityLevel.Hidden),
            Plain("Qty", "decimal"),
            Formula("Total", "decimal", "Qty * Cost"));

        isValid.Should().BeFalse();
        errors.Should().Contain(e => e.Contains("'Total'") && e.Contains("more widely readable"));
    }

    [Fact]
    public void A_formula_over_a_role_restricted_input_follows_the_role_rule_of_a_function_rule()
    {
        var (isValid, errors) = Save(
            Guarded(Plain("Salary", "decimal"), SensitivityLevel.Sensitive, "HR"),
            Plain("Bonus", "decimal"),
            Guarded(Formula("Total", "decimal", "Salary + Bonus"), SensitivityLevel.Sensitive, "HR", "Finance"));

        isValid.Should().BeFalse();
        errors.Should().ContainSingle(e => e.Contains("'Total'") && e.Contains("'Salary'") && e.Contains("'Finance'"));
    }

    [Fact]
    public void An_upsert_narrowing_an_input_under_a_stored_formula_is_refused()
    {
        FieldDefinition[] Fields(SensitivityLevel inputLevel) =>
        [
            Guarded(Plain("Salary", "decimal"), inputLevel, "HR"),
            Plain("Bonus", "decimal"),
            Guarded(Formula("Total", "decimal", "Salary + Bonus"), SensitivityLevel.Sensitive),
        ];

        var errors = ContentTypeUpsert.DefinitionErrors(
            Validator, TypeOf(Fields(SensitivityLevel.Sensitive)), TypeOf(Fields(SensitivityLevel.Hidden)), 100);

        errors.Should().Contain(e => e.Contains("'Total'") && e.Contains("'Salary'"));
    }

    [Fact]
    public void An_upsert_writing_a_stored_formula_back_unchanged_is_accepted()
    {
        errors(Order()).Should().BeEmpty();

        static List<string> errors(FieldDefinition[] fields) =>
            ContentTypeUpsert.DefinitionErrors(Validator, TypeOf(fields), TypeOf(Order()), 100);
    }

    [Fact]
    public void A_function_rule_alone_still_validates_as_it_did()
    {
        var byFn = new FieldDefinition
        {
            Name = "Total", DisplayName = "Total", Type = "decimal",
            Computed = new ComputedDefinition { Fn = "add", Args = ["Qty", "Cost"], Round = 2 },
        };

        var (isValid, errors) = Save(Plain("Qty", "decimal"), Plain("Cost", "decimal"), byFn);

        errors.Should().BeEmpty();
        isValid.Should().BeTrue();
    }

    [Fact]
    public void A_type_stored_by_5_9_0_loads_unchanged()
    {
        const string stored = """
            {
              "name": "timeEntry",
              "displayName": "Time entry",
              "fields": [
                { "name": "A", "displayName": "A", "type": "decimal" },
                { "name": "B", "displayName": "B", "type": "decimal" },
                { "name": "Sum", "displayName": "Sum", "type": "decimal", "computed": { "fn": "add", "args": ["A", "B"], "round": 2 } }
              ]
            }
            """;

        var type = JsonSerializer.Deserialize<ContentTypeDefinition>(stored, ApiJson.Options)!;
        var sum = type.Fields.Single(f => f.Name == "Sum");

        sum.Computed!.Expression.Should().BeNull();
        sum.Computed.Fn.Should().Be("add");
        sum.Computed.Args.Should().Equal("A", "B");
        sum.Computed.Round.Should().Be(2);
        Validator.Validate(type.Name, type.DisplayName, type.Fields).IsValid.Should().BeTrue();

        var data = new Dictionary<string, object> { ["A"] = 1.005m, ["B"] = 2m };
        ComputedFields.Apply(type.Fields, data);
        data["Sum"].Should().Be(3.01m);
        ComputedRecompute.Needed(type.Fields, type).Should().BeFalse("a type with no formula has nothing to recompute");
    }

    [Fact]
    public void A_formula_is_new_or_changed_only_when_it_differs_from_the_stored_one()
    {
        var stored = Order();

        ComputedRecompute.Needed(stored, TypeOf(Order())).Should().BeFalse("the same formula");
        ComputedRecompute.Needed(stored, TypeOf(Order("Qty + Cost"))).Should().BeTrue("a changed formula");
        ComputedRecompute.Needed(null, TypeOf(Order())).Should().BeTrue("a formula on a type that had none");
        ComputedRecompute.Needed(
            [Plain("Qty", "decimal"), Plain("Cost", "decimal")], TypeOf(Order())).Should().BeTrue("a new formula field");

        var fnOnly = new FieldDefinition
        {
            Name = "Total", DisplayName = "Total", Type = "decimal",
            Computed = new ComputedDefinition { Fn = "add", Args = ["Qty", "Cost"] },
        };
        ComputedRecompute.Needed([Plain("Qty", "decimal"), Plain("Cost", "decimal")], TypeOf(Plain("Qty", "decimal"), Plain("Cost", "decimal"), fnOnly))
            .Should().BeFalse("a function rule keeps today's behaviour and starts no backfill");
    }

    // Integration: a type saved through the API in its own tenant.

    private async Task<(string Slug, HttpClient Admin)> TenantAsync()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        return (slug, await TenantProfileSeed.AdminInAsync(_fixture, slug, Ct));
    }

    private static string NewName() => "expr" + Guid.NewGuid().ToString("n")[..10];

    private static object[] ApiFields(string expression) =>
    [
        new { name = "Qty", type = "decimal" },
        new { name = "Cost", type = "decimal" },
        new { name = "Total", type = "decimal", computed = new { expression, round = 2 } },
    ];

    private static async Task<HttpResponseMessage> CreateTypeAsync(HttpClient admin, string name, object[] fields) =>
        await admin.PostAsJsonAsync("/api/content-types", new { name, displayName = name, isPubliclyDeliverable = true, fields }, Ct);

    private static async Task CreateEntryAsync(HttpClient admin, string type, decimal qty, decimal cost)
    {
        var res = await admin.PostAsJsonAsync("/api/contents", new
        {
            contentType = type, data = new Dictionary<string, object> { ["Qty"] = qty, ["Cost"] = cost },
        }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
    }

    private async Task<List<Content>> EntriesAsync(string slug, string type)
    {
        await using var session = _fixture.Services.GetRequiredService<IDocumentStore>().QuerySession(slug);
        return (await session.Query<Content>().Where(c => c.ContentType == type).ToListAsync(Ct)).ToList();
    }

    /// <summary>The recompute the definition write started, run to completion here and not left to the job queue.</summary>
    private async Task RunRecomputeAsync(string slug, string type)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var configuration = _fixture.Services.GetRequiredService<IConfiguration>();
        Guid id;
        await using (var session = store.QuerySession(slug))
        {
            var change = await session.Query<FieldChange>()
                .Where(c => c.ContentType == type && c.Kind == FieldChangeKind.Recompute)
                .FirstOrDefaultAsync(Ct);
            change.Should().NotBeNull("a changed formula on a type with entries starts a recompute");
            id = change!.Id;
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if ((await FieldChanges.RunAsync(store, configuration, slug, id, maxBatches: null, Ct))?.State == FieldChangeState.Completed)
                {
                    return;
                }
            }
            catch (Exception e) when (TestHelpers.LostConcurrencyCheck(e))
            {
                // The queued job committed the same batch first; the next run reads its progress.
            }
        }

        throw new Xunit.Sdk.XunitException($"Field change {id} did not complete.");
    }

    [Fact]
    public async Task A_type_with_a_formula_is_saved_and_a_mixed_rule_is_a_400()
    {
        var (_, admin) = await TenantAsync();

        var saved = await CreateTypeAsync(admin, NewName(), ApiFields("Qty * Cost"));
        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));

        var mixed = await CreateTypeAsync(admin, NewName(),
        [
            new { name = "Qty", type = "decimal" },
            new { name = "Cost", type = "decimal" },
            new { name = "Total", type = "decimal", computed = new { fn = "add", args = new[] { "Qty", "Cost" }, expression = "Qty * Cost" } },
        ]);
        mixed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await mixed.Content.ReadAsStringAsync(Ct)).Should().Contain("'Total'").And.Contain("one or the other");
    }

    [Fact]
    public async Task A_stored_formula_reads_back_as_written()
    {
        var (slug, admin) = await TenantAsync();
        var type = NewName();
        (await CreateTypeAsync(admin, type, ApiFields("Qty * Cost"))).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var session = _fixture.Services.GetRequiredService<IDocumentStore>().QuerySession(slug);
        var stored = await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type, Ct);

        stored.Fields.Single(f => f.Name == "Total").Computed!.Expression.Should().Be("Qty * Cost");
    }

    [Fact]
    public async Task Renaming_a_field_a_formula_names_is_refused_and_so_is_removing_it()
    {
        var (_, admin) = await TenantAsync();
        var type = NewName();
        (await CreateTypeAsync(admin, type, ApiFields("Qty * Cost"))).StatusCode.Should().Be(HttpStatusCode.OK);

        var rename = await admin.PostAsJsonAsync($"/api/content-types/{type}/fields/Cost/change", new { newName = "Price" }, Ct);
        rename.StatusCode.Should().Be(HttpStatusCode.BadRequest, await rename.Content.ReadAsStringAsync(Ct));
        (await rename.Content.ReadAsStringAsync(Ct)).Should().Contain("Total");

        var remove = await admin.PostAsJsonAsync(
            $"/api/content-types/{type}/fields/Cost/change", new { remove = true, allowDestructive = true }, Ct);
        remove.StatusCode.Should().Be(HttpStatusCode.BadRequest, await remove.Content.ReadAsStringAsync(Ct));
        (await remove.Content.ReadAsStringAsync(Ct)).Should().Contain("the formula of 'Total'");
    }

    [Fact]
    public async Task Raising_the_sensitivity_of_a_field_a_public_formula_reads_follows_the_existing_rule()
    {
        var (_, admin) = await TenantAsync();
        var type = NewName();
        (await CreateTypeAsync(admin, type, ApiFields("Qty * Cost"))).StatusCode.Should().Be(HttpStatusCode.OK);

        var raised = await admin.PutAsJsonAsync(
            $"/api/content-types/{type}/fields/Cost/sensitivity", new { sensitivity = "Hidden" }, Ct);

        raised.StatusCode.Should().Be(HttpStatusCode.BadRequest, await raised.Content.ReadAsStringAsync(Ct));
        (await raised.Content.ReadAsStringAsync(Ct)).Should().Contain("'Total'").And.Contain("more widely readable");
    }

    [Fact]
    public async Task A_changed_formula_in_a_config_apply_backfills_the_entries_already_stored()
    {
        var (slug, admin) = await TenantAsync();
        var type = NewName();
        (await CreateTypeAsync(admin, type, ApiFields("Qty + Cost"))).StatusCode.Should().Be(HttpStatusCode.OK);
        await CreateEntryAsync(admin, type, 3m, 4m);
        await CreateEntryAsync(admin, type, 10m, 0.5m);
        (await EntriesAsync(slug, type)).Select(e => e.Data["Total"].ToString()).Should().BeEquivalentTo("7", "10.5");

        var applied = await admin.PostAsJsonAsync("/api/config/apply", new
        {
            document = new
            {
                version = 1,
                contentTypes = new Dictionary<string, object>
                {
                    [type] = new { name = type, displayName = type, isPubliclyDeliverable = true, fields = ApiFields("Qty * Cost") },
                },
            },
            allowDestructive = true,
        }, Ct);
        applied.StatusCode.Should().Be(HttpStatusCode.OK, await applied.Content.ReadAsStringAsync(Ct));

        await RunRecomputeAsync(slug, type);

        // Decimal multiplication keeps the scale, as Fn multiply does: 10 * 0.5 is 5.0.
        (await EntriesAsync(slug, type)).Select(e => e.Data["Total"].ToString()).Should().BeEquivalentTo("12", "5.0");
    }

    [Fact]
    public async Task A_bundle_import_treats_a_changed_formula_as_a_computation_change()
    {
        var (slug, admin) = await TenantAsync();
        var type = NewName();
        (await CreateTypeAsync(admin, type, ApiFields("Qty + Cost"))).StatusCode.Should().Be(HttpStatusCode.OK);
        await CreateEntryAsync(admin, type, 3m, 4m);

        var imported = await admin.PostAsJsonAsync("/api/portability/import", new
        {
            dryRun = false,
            contentTypes = new[] { new { name = type, displayName = type, isPubliclyDeliverable = true, fields = ApiFields("Qty * Cost") } },
            contents = Array.Empty<object>(),
        }, Ct);
        imported.StatusCode.Should().Be(HttpStatusCode.OK, await imported.Content.ReadAsStringAsync(Ct));

        await RunRecomputeAsync(slug, type);

        (await EntriesAsync(slug, type)).Single().Data["Total"].ToString().Should().Be("12");
    }

    [Fact]
    public async Task A_config_apply_that_leaves_the_formula_alone_starts_no_recompute()
    {
        var (slug, admin) = await TenantAsync();
        var type = NewName();
        (await CreateTypeAsync(admin, type, ApiFields("Qty + Cost"))).StatusCode.Should().Be(HttpStatusCode.OK);
        await CreateEntryAsync(admin, type, 3m, 4m);

        var applied = await admin.PostAsJsonAsync("/api/config/apply", new
        {
            document = new
            {
                version = 1,
                contentTypes = new Dictionary<string, object>
                {
                    [type] = new { name = type, displayName = type, isPubliclyDeliverable = true, fields = ApiFields("Qty + Cost") },
                },
            },
            allowDestructive = true,
        }, Ct);
        applied.StatusCode.Should().Be(HttpStatusCode.OK, await applied.Content.ReadAsStringAsync(Ct));

        await using var session = _fixture.Services.GetRequiredService<IDocumentStore>().QuerySession(slug);
        (await session.Query<FieldChange>().CountAsync(c => c.ContentType == type, Ct)).Should().Be(0);
    }
}

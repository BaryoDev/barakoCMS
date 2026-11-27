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
using BarakoCMS.Tests.Jobs;

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

    public ComputedExpressionSchemaTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;

        // The recompute tests wait on the queue's wake. A host started earlier in the process owns
        // the static job queues until they are taken back, and its worker never sees this database.
        FastEndpointsResolver.ClaimJobQueues(fixture.Services);
    }

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

    /// <summary>200 formulas, each reading the one before and padded to 1900 characters, declared last first.</summary>
    private static List<FieldDefinition> Chain(int length)
    {
        var padding = new string(' ', 1900);
        return Enumerable.Range(0, length).Reverse()
            .Select(i => Formula($"F{i}", "decimal", (i == 0 ? "Qty + 1" : $"F{i - 1} + 1") + padding))
            .ToList();
    }

    [Fact]
    public void A_long_chain_of_formulas_declared_in_reverse_is_put_in_dependency_order_with_one_parse_each()
    {
        var chain = Chain(200);

        var before = barakoCMS.Core.Expressions.ExpressionEngine.ReadsOnThisThread;
        var ordered = ComputedFields.InOrder(chain);
        var parses = barakoCMS.Core.Expressions.ExpressionEngine.ReadsOnThisThread - before;

        ordered.Select(f => f.Name).Should().Equal(Enumerable.Range(0, 200).Select(i => $"F{i}"));
        parses.Should().Be(200, "each formula is read once, not once for every field placed before it");
    }

    [Fact]
    public void A_write_over_a_long_chain_parses_each_formula_once_and_works_every_value_out()
    {
        var fields = new List<FieldDefinition> { Plain("Qty", "decimal") };
        fields.AddRange(Chain(200));
        var data = new Dictionary<string, object> { ["Qty"] = 1m };

        var before = barakoCMS.Core.Expressions.ExpressionEngine.ReadsOnThisThread;
        ComputedFields.Apply(fields, data);
        var parses = barakoCMS.Core.Expressions.ExpressionEngine.ReadsOnThisThread - before;

        Convert.ToDecimal(data["F199"]).Should().Be(201m);
        Convert.ToDecimal(data["F0"]).Should().Be(2m);
        parses.Should().Be(200, "the order and the value share one read of each formula");
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

    private static List<UniquenessRule> Unique(string rule, params string[] fields) =>
        [new UniquenessRule { Name = rule, Fields = fields.ToList() }];

    [Fact]
    public void A_uniqueness_rule_over_a_formula_field_is_refused_with_the_rule_and_the_field()
    {
        var (isValid, errors) = ((IContentTypeValidatorService)Validator).ValidateUniqueness(Unique("OneTotal", "Total"), Order().ToList(), null);

        isValid.Should().BeFalse();
        errors.Should().ContainSingle(e => e.Contains("'OneTotal'") && e.Contains("'Total'") && e.Contains("formula"));
    }

    [Fact]
    public void A_uniqueness_rule_over_a_function_rule_field_is_still_accepted()
    {
        var total = new FieldDefinition
        {
            Name = "Total", DisplayName = "Total", Type = "decimal",
            Computed = new ComputedDefinition { Fn = "add", Args = ["Qty", "Cost"] },
        };

        var (isValid, errors) = ((IContentTypeValidatorService)Validator).ValidateUniqueness(
            Unique("OneTotal", "Total"), [Plain("Qty", "decimal"), Plain("Cost", "decimal"), total], null);

        errors.Should().BeEmpty();
        isValid.Should().BeTrue();
    }

    [Fact]
    public void An_upsert_turning_a_function_rule_into_a_formula_under_a_stored_uniqueness_rule_is_refused()
    {
        FieldDefinition[] Fields(ComputedDefinition computed) =>
        [
            Plain("Qty", "decimal"),
            Plain("Cost", "decimal"),
            new() { Name = "Total", DisplayName = "Total", Type = "decimal", Computed = computed },
        ];

        var stored = TypeOf(Fields(new ComputedDefinition { Fn = "add", Args = ["Qty", "Cost"] }));
        stored.Uniqueness = Unique("OneTotal", "Total");
        var incoming = TypeOf(Fields(new ComputedDefinition { Expression = "Qty * Cost" }));

        // Config apply and bundle import both reach this through ContentTypeUpsert.ErrorsAsync.
        var errors = ContentTypeUpsert.UniquenessChanges(incoming, stored, ContentTypeUpsert.Wording.Config).ToList();

        errors.Should().ContainSingle(e => e.Contains("'OneTotal'") && e.Contains("'Total'") && e.Contains("formula"));
    }

    [Fact]
    public void An_upsert_leaving_a_function_rule_under_a_stored_uniqueness_rule_is_accepted()
    {
        FieldDefinition[] Fields() =>
        [
            Plain("Qty", "decimal"),
            Plain("Cost", "decimal"),
            new() { Name = "Total", DisplayName = "Total", Type = "decimal", Computed = new ComputedDefinition { Fn = "add", Args = ["Qty", "Cost"] } },
        ];

        var stored = TypeOf(Fields());
        stored.Uniqueness = Unique("OneTotal", "Total");

        ContentTypeUpsert.DefinitionErrors(Validator, TypeOf(Fields()), stored, 100).Should().BeEmpty();
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
    public async Task Converting_a_formula_field_to_a_type_its_result_does_not_fit_is_a_400_and_starts_nothing()
    {
        var (slug, admin) = await TenantAsync();
        var type = NewName();
        (await CreateTypeAsync(admin, type, ApiFields("Qty * Cost"))).StatusCode.Should().Be(HttpStatusCode.OK);

        var convert = await admin.PostAsJsonAsync($"/api/content-types/{type}/fields/Total/change", new { toType = "string" }, Ct);

        convert.StatusCode.Should().Be(HttpStatusCode.BadRequest, await convert.Content.ReadAsStringAsync(Ct));
        (await convert.Content.ReadAsStringAsync(Ct)).Should().Contain("'Total'").And.Contain("gives number").And.Contain("not string");
        await using var session = _fixture.Services.GetRequiredService<IDocumentStore>().QuerySession(slug);
        var stored = await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type, Ct);
        stored.Fields.Single(f => f.Name == "Total").Type.Should().Be("decimal");
        (await session.Query<FieldChange>().AnyAsync(c => c.ContentType == type, Ct)).Should().BeFalse();
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
    public async Task A_bundle_import_that_changes_a_formula_commits_the_recompute_job_with_the_change()
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

        await AssertJobCommittedAsync(slug, type);
    }

    [Fact]
    public async Task A_config_apply_that_changes_a_formula_commits_the_recompute_job_with_the_change()
    {
        var (slug, admin) = await TenantAsync();
        var type = NewName();
        (await CreateTypeAsync(admin, type, ApiFields("Qty + Cost"))).StatusCode.Should().Be(HttpStatusCode.OK);
        await CreateEntryAsync(admin, type, 3m, 4m);

        var applied = await admin.PostAsJsonAsync("/api/config/apply", ConfigDocument(type, ApiFields("Qty * Cost"), allowDestructive: true), Ct);
        applied.StatusCode.Should().Be(HttpStatusCode.OK, await applied.Content.ReadAsStringAsync(Ct));

        await AssertJobCommittedAsync(slug, type);
    }

    [Fact]
    public async Task A_config_apply_that_changes_a_formula_has_the_queue_work_it_out_with_no_hand_run()
    {
        var (slug, admin) = await TenantAsync();
        var type = NewName();
        (await CreateTypeAsync(admin, type, ApiFields("Qty + Cost"))).StatusCode.Should().Be(HttpStatusCode.OK);
        await CreateEntryAsync(admin, type, 3m, 4m);

        var applied = await admin.PostAsJsonAsync("/api/config/apply", ConfigDocument(type, ApiFields("Qty * Cost"), allowDestructive: true), Ct);
        applied.StatusCode.Should().Be(HttpStatusCode.OK, await applied.Content.ReadAsStringAsync(Ct));

        await CompletedWithinProbeAsync(slug, type);
        (await EntriesAsync(slug, type)).Single().Data["Total"].ToString().Should().Be("12");
    }

    [Fact]
    public async Task A_bundle_import_that_changes_a_formula_has_the_queue_work_it_out_with_no_hand_run()
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

        await CompletedWithinProbeAsync(slug, type);
        (await EntriesAsync(slug, type)).Single().Data["Total"].ToString().Should().Be("12");
    }

    /// <summary>
    /// A wake at the batch's flush finds nothing, as the job is not committed yet. Holding the flush
    /// open makes that certain: the worker looks, finds nothing and waits for its next storage poll,
    /// 60 s away. Only a wake after the commit finishes the change inside the deadline.
    /// </summary>
    [Fact]
    public async Task A_recompute_queued_in_a_content_batch_is_woken_after_the_batch_commits_not_at_its_flush()
    {
        var (slug, admin) = await TenantAsync();
        var type = NewName();
        (await CreateTypeAsync(admin, type, ApiFields("Qty + Cost"))).StatusCode.Should().Be(HttpStatusCode.OK);
        await CreateEntryAsync(admin, type, 3m, 4m);

        await using var scope = _fixture.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<barakoCMS.Infrastructure.Multitenancy.TenantContext>().Slug = slug;
        var runner = scope.ServiceProvider.GetRequiredService<IContentBatchRunner>();
        await runner.RunAsync(async (services, ct) =>
        {
            var session = services.GetRequiredService<IDocumentSession>();
            (await FieldChanges.StartAsync(session, new FieldChange { ContentType = type, Field = "Total", Kind = FieldChangeKind.Recompute }, ct))
                .Should().BeNull("no change is running on the type");

            // After the job's own listener, so the worker has looked and gone back to waiting
            // before the batch commits.
            session.Listeners.Add(new HoldTheFlush(TimeSpan.FromSeconds(2)));
            return new ContentBatchOutcome<bool>(true, Commit: true);
        }, Ct);

        await CompletedWithinProbeAsync(slug, type);
    }

    private sealed class HoldTheFlush(TimeSpan hold) : DocumentSessionListenerBase
    {
        public override Task AfterCommitAsync(IDocumentSession session, Marten.Services.IChangeSet commit, CancellationToken token) =>
            Task.Delay(hold, token);
    }

    /// <summary>
    /// The worker polls storage every 60 s, so a change still running at this deadline was never
    /// woken after its commit.
    /// </summary>
    private async Task CompletedWithinProbeAsync(string slug, string type)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            await using var session = store.QuerySession(slug);
            var change = await session.Query<FieldChange>()
                .SingleAsync(c => c.ContentType == type && c.Kind == FieldChangeKind.Recompute, Ct);
            if (change.State == FieldChangeState.Completed)
            {
                return;
            }

            await Task.Delay(100, Ct);
        }

        throw new Xunit.Sdk.XunitException($"The recompute of {type} did not complete within 20 s of the commit.");
    }

    [Fact]
    public async Task A_plan_and_a_refused_apply_that_change_a_formula_commit_no_job()
    {
        var (slug, admin) = await TenantAsync();
        var type = NewName();
        object[] withNote = [.. ApiFields("Qty + Cost"), new { name = "Note", type = "string" }];
        (await CreateTypeAsync(admin, type, withNote)).StatusCode.Should().Be(HttpStatusCode.OK);
        await CreateEntryAsync(admin, type, 3m, 4m);
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var before = await QueuedJobs.FieldChangeCountAsync(store, slug, Ct);

        // Dropping Note is destructive, so the apply is refused and rolled back; a plan always is.
        var document = ConfigDocument(type, ApiFields("Qty * Cost"), allowDestructive: false);
        var plan = await admin.PostAsJsonAsync("/api/config/plan", document, Ct);
        plan.StatusCode.Should().Be(HttpStatusCode.OK, await plan.Content.ReadAsStringAsync(Ct));
        var refused = await admin.PostAsJsonAsync("/api/config/apply", document, Ct);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict, await refused.Content.ReadAsStringAsync(Ct));

        await using var session = store.QuerySession(slug);
        (await session.Query<FieldChange>().CountAsync(c => c.ContentType == type, Ct)).Should().Be(0, "the apply was rolled back");
        (await QueuedJobs.FieldChangeCountAsync(store, slug, Ct)).Should().Be(before, "no job may outlive the change it was queued for");
    }

    private static Dictionary<string, object> ConfigDocument(string type, object[] fields, bool allowDestructive) => new()
    {
        ["document"] = new
        {
            version = 1,
            contentTypes = new Dictionary<string, object>
            {
                [type] = new { name = type, displayName = type, isPubliclyDeliverable = true, fields },
            },
        },
        ["allowDestructive"] = allowDestructive,
    };

    /// <summary>The change the write started is saved running, and its job committed with it, in its tenant.</summary>
    private async Task AssertJobCommittedAsync(string slug, string type)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(slug);
        var change = await session.Query<FieldChange>().SingleAsync(c => c.ContentType == type && c.Kind == FieldChangeKind.Recompute, Ct);

        var jobs = await QueuedJobs.ForChangeAsync(store, change.Id, Ct);

        jobs.Should().ContainSingle("a change saved running with no job locks its type for good");
        jobs[0].TenantId.Should().Be(slug);
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

    [Fact]
    public async Task A_type_declaring_a_uniqueness_rule_over_a_formula_field_is_a_400()
    {
        var (_, admin) = await TenantAsync();

        var res = await admin.PostAsJsonAsync("/api/content-types", new
        {
            name = NewName(), displayName = "Order", isPubliclyDeliverable = true, fields = ApiFields("Qty * Cost"),
            uniqueness = new[] { new { name = "OneTotal", fields = new[] { "Total" } } },
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync(Ct));
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("'OneTotal'").And.Contain("'Total'").And.Contain("formula");
    }

    [Fact]
    public async Task Setting_a_uniqueness_rule_over_a_formula_field_is_a_400_and_stores_nothing()
    {
        var (slug, admin) = await TenantAsync();
        var type = NewName();
        (await CreateTypeAsync(admin, type, ApiFields("Qty * Cost"))).StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await admin.PutAsJsonAsync(
            $"/api/content-types/{type}/uniqueness",
            new { uniqueness = new[] { new { name = "OneTotal", fields = new[] { "Total" } } } }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync(Ct));
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("'OneTotal'").And.Contain("'Total'").And.Contain("formula");

        await using var session = _fixture.Services.GetRequiredService<IDocumentStore>().QuerySession(slug);
        (await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type, Ct)).Uniqueness.Should().BeNullOrEmpty();
    }
}

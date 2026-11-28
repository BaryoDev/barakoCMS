using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Core.Interfaces;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Rollups;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Probe = BarakoCMS.Tests.TokenFieldProbe;
using R = BarakoCMS.Tests.RollupProbe;

namespace BarakoCMS.Tests;

/// <summary>
/// What a type is refused for when it declares a rollup, and what a change to the type a rollup
/// summarises is refused for, on every path that writes a definition.
/// </summary>
/// <remarks>
/// A line type is stored straight into the default tenant under a name no other test uses. The
/// header is created through the API, so the save-time checks are the thing under test.
/// </remarks>
[Collection("Integration1")]
public class RollupDefinitionTests
{
    private static readonly ContentTypeValidatorService Validator = new();

    private readonly IntegrationTestFixture _fixture;

    public RollupDefinitionTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        FastEndpointsResolver.ClaimJobQueues(fixture.Services);
    }

    private static CancellationToken Ct => R.Ct;

    // Unit: what the type's own fields show.

    private static FieldDefinition Count(Action<FieldDefinition>? change = null)
    {
        var field = R.Rollup("Count", "int", "line", "Teacher", "count");
        change?.Invoke(field);
        return field;
    }

    private static FieldDefinition Sum(Action<FieldDefinition>? change = null)
    {
        var field = R.Rollup("Total", "decimal", "line", "Teacher", "sum", "Hours");
        change?.Invoke(field);
        return field;
    }

    [Fact]
    public void A_count_and_a_sum_with_what_they_need_are_accepted()
    {
        RollupFields.DefinitionErrors([Count(), Sum(f => f.Rollup!.Round = 2)]).Should().BeEmpty();
    }

    [Theory]
    [InlineData("computed", "is a rollup and is computed")]
    [InlineData("fn", "is not a function")]
    [InlineData("type", "must be of type int")]
    [InlineData("from", "must name the content type")]
    [InlineData("via", "must name the reference field")]
    [InlineData("countfield", "takes no rollup.field")]
    [InlineData("sumfield", "must name the field it summarises")]
    [InlineData("roundmax", "takes no round")]
    [InlineData("roundrange", "rounding is 0 to 10")]
    [InlineData("multiple", "cannot hold a list")]
    [InlineData("required", "cannot be required")]
    [InlineData("default", "takes no default value")]
    [InlineData("ownerhidden", "cannot be hidden by its owner")]
    public void A_rollup_that_breaks_a_rule_of_its_own_is_refused(string kind, string expected)
    {
        var field = kind switch
        {
            "computed" => Count(f => f.Computed = new ComputedDefinition { Expression = "1 + 1" }),
            "fn" => Count(f => f.Rollup!.Fn = "median"),
            "type" => Count(f => f.Type = "decimal"),
            "from" => Count(f => f.Rollup!.From = ""),
            "via" => Count(f => f.Rollup!.Via = ""),
            "countfield" => Count(f => f.Rollup!.Field = "Hours"),
            "sumfield" => Sum(f => f.Rollup!.Field = null),
            "roundmax" => R.Rollup("Peak", "decimal", "line", "Teacher", "max", "Hours", round: 2),
            "roundrange" => Sum(f => f.Rollup!.Round = 11),
            "multiple" => Count(f => f.Multiple = true),
            "required" => Count(f => f.IsRequired = true),
            "default" => Count(f => f.DefaultValue = 1),
            _ => Count(f => f.ShownWhenListedIn = "Owners"),
        };

        RollupFields.DefinitionErrors([field]).Should().Contain(e => e.Contains(expected));
    }

    // Unit: the window of a rollup.

    private static FieldDefinition Windowed(string key = "createdAt", string word = "thisMonth") =>
        R.Rollup("Recent", "int", "line", "Teacher", "count", windowKey: key, window: word);

    [Fact]
    public void A_window_on_createdat_or_a_field_with_a_known_word_is_accepted_in_any_case()
    {
        RollupFields.DefinitionErrors([Windowed(), Windowed("CREATEDAT", "LAST7DAYS"), Windowed("Day", "lastMonth"), Windowed("Day", " today ")])
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("twokeys", "has a where with 2 keys")]
    [InlineData("nokeys", "has a where with 0 keys")]
    [InlineData("badword", "which is not a window")]
    [InlineData("blankword", "which is not a window")]
    [InlineData("blankkey", "has a where with no field")]
    public void A_window_that_is_not_one_key_and_one_word_is_refused(string kind, string expected)
    {
        var field = kind switch
        {
            "twokeys" => Windowed(),
            "nokeys" => Windowed(),
            "badword" => Windowed("Day", "yesterday"),
            "blankword" => Windowed("Day", ""),
            _ => Windowed(" ", "today"),
        };
        if (kind == "twokeys")
            field.Rollup!.Where!["Day"] = "today";
        if (kind == "nokeys")
            field.Rollup!.Where!.Clear();

        RollupFields.DefinitionErrors([field]).Should().Contain(e => e.Contains(expected));
    }

    [Fact]
    public void A_refused_word_names_the_words_that_are_accepted()
    {
        var errors = RollupFields.DefinitionErrors([Windowed("Day", "yesterday")]);

        errors.Should().Contain(e => RelativeWords.All(w => e.Contains(w)));
    }

    private static readonly string[] RelativeWords = ["today", "last7Days", "last30Days", "thisMonth", "lastMonth", "thisYear"];

    [Fact]
    public void A_where_change_starts_a_recount_and_a_change_of_casing_does_not()
    {
        var plain = new[] { R.Rollup("Recent", "int", "line", "Teacher", "count") };
        var windowed = new[] { Windowed("Day", "last7Days") };
        var recased = new[] { Windowed("day", "LAST7DAYS") };
        var other = new[] { Windowed("Day", "last30Days") };

        RollupRecompute.Changed(plain, windowed).Should().Equal("Recent");
        RollupRecompute.Changed(windowed, plain).Should().Equal("Recent");
        RollupRecompute.Changed(windowed, recased).Should().BeEmpty();
        RollupRecompute.Changed(windowed, other).Should().Equal("Recent");
    }

    [Fact]
    public void The_type_validator_applies_the_same_rules()
    {
        var (valid, errors) = Validator.Validate("order", "Order", [R.Plain("Name", "string"), Count(f => f.Rollup!.Fn = "median")]);

        valid.Should().BeFalse();
        errors.Should().Contain(e => e.Contains("is not a function"));
    }

    // Integration: a header saved through the API.

    private static (string Header, string Line) Names() =>
        ("rh" + Guid.NewGuid().ToString("n")[..10], "rl" + Guid.NewGuid().ToString("n")[..10]);

    private async Task StoreAsync(params ContentTypeDefinition[] types)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        foreach (var type in types)
            session.Store(type);
        await session.SaveChangesAsync(Ct);
    }

    private static ContentTypeDefinition Type(string name, params FieldDefinition[] fields) =>
        new() { Id = Guid.NewGuid(), Name = name, DisplayName = name, Fields = fields.ToList() };

    /// <summary>The line type of a header that is not stored yet.</summary>
    private async Task<(string Header, string Line)> LineAsync(
        SensitivityLevel teacher = SensitivityLevel.Public, SensitivityLevel hours = SensitivityLevel.Public, params FieldDefinition[] extra)
    {
        var (header, line) = Names();
        await StoreAsync(Type(line, [R.Reference("Teacher", header, teacher), R.Plain("Hours", "decimal", hours), .. extra]));
        return (header, line);
    }

    /// <summary>A stored header and line, the header with a count and a sum at <paramref name="level"/>.</summary>
    private async Task<(string Header, string Line)> StoredPairAsync(
        SensitivityLevel level = SensitivityLevel.Public, SensitivityLevel teacher = SensitivityLevel.Public, SensitivityLevel hours = SensitivityLevel.Public)
    {
        var (header, line) = await LineAsync(teacher, hours);
        await StoreAsync(Type(
            header,
            R.Plain("Name", "string"),
            R.Rollup("Count", "int", line, "Teacher", "count", null, level),
            R.Rollup("Total", "decimal", line, "Teacher", "sum", "Hours", level)));
        return (header, line);
    }

    private static object RollupField(
        string name, string type, string from, string via, string fn, string? field = null, string sensitivity = "Public") =>
        new { name, type, sensitivity, rollup = new { from, via, fn, field } };

    private async Task<HttpClient> AdminAsync() => R.Client(_fixture, await Probe.AdminAsync(_fixture));

    private async Task<HttpResponseMessage> CreateAsync(HttpClient client, string name, params object[] fields) =>
        await client.PostAsJsonAsync("/api/content-types", new { name, displayName = name, fields = WithName(fields) }, Ct);

    private static object[] WithName(object[] fields) => [new { name = "Name", type = "string" }, .. fields];

    private static async Task<string> BodyAsync(HttpResponseMessage res) => await res.Content.ReadAsStringAsync(Ct);

    private async Task<FieldDefinition?> StoredFieldAsync(string type, string field) =>
        (await Probe.StoredTypeAsync(_fixture, type)).Fields.FirstOrDefault(f => f.Name == field);

    private async Task AssertNotStoredAsync(string type)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        (await session.Query<ContentTypeDefinition>().AnyAsync(d => d.Name == type, Ct)).Should().BeFalse("a refused create stores nothing");
    }

    private async Task AssertRefusedAsync(HttpResponseMessage res, string expected, HttpStatusCode status = HttpStatusCode.BadRequest)
    {
        var body = await BodyAsync(res);
        res.StatusCode.Should().Be(status, body);
        body.Should().Contain(expected);
    }

    [Fact]
    public async Task Create_saves_a_rollup_the_stored_types_support()
    {
        var (header, line) = await LineAsync();

        var res = await CreateAsync(
            await AdminAsync(), header,
            RollupField("Count", "int", line, "Teacher", "count"),
            RollupField("Total", "decimal", line, "Teacher", "sum", "Hours"));

        res.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(res));
        (await StoredFieldAsync(header, "Total"))!.Rollup!.From.Should().Be(line);
    }

    [Fact]
    public async Task Create_refuses_a_rollup_over_a_type_that_does_not_exist()
    {
        var (header, _) = await LineAsync();

        var res = await CreateAsync(await AdminAsync(), header, RollupField("Count", "int", "nosuchline", "Teacher", "count"));

        await AssertRefusedAsync(res, "is not a content type");
    }

    [Fact]
    public async Task Create_refuses_a_via_the_line_type_does_not_declare()
    {
        var (header, line) = await LineAsync();

        var res = await CreateAsync(await AdminAsync(), header, RollupField("Count", "int", line, "Nobody", "count"));

        await AssertRefusedAsync(res, "does not declare");
    }

    [Fact]
    public async Task Create_refuses_a_via_that_is_not_a_reference_to_the_header()
    {
        var (header, line) = await LineAsync();

        var res = await CreateAsync(await AdminAsync(), header, RollupField("Count", "int", line, "Hours", "count"));

        await AssertRefusedAsync(res, "must be a single reference");
    }

    [Fact]
    public async Task Create_refuses_a_rollup_over_a_rollup()
    {
        var (header, line) = await LineAsync(extra: R.Rollup("Sub", "int", "elsewhere", "Link", "count"));

        var res = await CreateAsync(await AdminAsync(), header, RollupField("Total", "int", line, "Teacher", "sum", "Sub"));

        await AssertRefusedAsync(res, "is or reads a rollup");
    }

    [Fact]
    public async Task Create_refuses_a_rollup_over_a_branch_scoped_type()
    {
        var (header, line) = Names();
        var scoped = Type(line, R.Reference("Teacher", header), R.Plain("Hours", "decimal"));
        scoped.BranchScoped = true;
        await StoreAsync(scoped);

        var res = await CreateAsync(await AdminAsync(), header, RollupField("Count", "int", line, "Teacher", "count"));

        await AssertRefusedAsync(res, "is branch scoped");
        await AssertNotStoredAsync(header);
    }

    [Fact]
    public async Task A_config_apply_that_turns_on_branch_scoping_for_a_counted_type_is_refused_and_leaves_it_alone()
    {
        var (header, line) = await StoredPairAsync();
        var config = new
        {
            document = new
            {
                version = 1,
                contentTypes = new Dictionary<string, object>
                {
                    [line] = new
                    {
                        name = line, displayName = line, branchScoped = true,
                        fields = new object[]
                        {
                            new { name = "Teacher", type = "reference", referenceType = header },
                            new { name = "Hours", type = "decimal" },
                        },
                    },
                },
            },
            allowDestructive = true,
        };

        var res = await (await AdminAsync()).PostAsJsonAsync("/api/config/apply", config, Ct);

        await AssertRefusedAsync(res, "is branch scoped");
        (await Probe.StoredTypeAsync(_fixture, line)).BranchScoped.Should().BeFalse();
    }

    [Fact]
    public async Task Create_refuses_a_declared_type_that_does_not_fit_the_summed_field()
    {
        var (header, line) = await LineAsync();

        var res = await CreateAsync(await AdminAsync(), header, RollupField("Total", "int", line, "Teacher", "sum", "Hours"));

        await AssertRefusedAsync(res, "must be of type decimal");
    }

    [Fact]
    public async Task Create_refuses_a_rollup_more_widely_readable_than_the_field_it_sums()
    {
        var (header, line) = await LineAsync(hours: SensitivityLevel.Sensitive);

        var res = await CreateAsync(await AdminAsync(), header, RollupField("Total", "decimal", line, "Teacher", "sum", "Hours"));

        await AssertRefusedAsync(res, "more widely readable than what it summarises");
    }

    [Fact]
    public async Task Create_refuses_a_public_count_over_a_sensitive_via()
    {
        var (header, line) = await LineAsync(teacher: SensitivityLevel.Sensitive);

        var res = await CreateAsync(await AdminAsync(), header, RollupField("Count", "int", line, "Teacher", "count"));

        await AssertRefusedAsync(res, "more widely readable than the reference");
        await AssertNotStoredAsync(header);
    }

    [Fact]
    public async Task Create_accepts_a_sensitive_count_over_a_sensitive_via()
    {
        var (header, line) = await LineAsync(teacher: SensitivityLevel.Sensitive);

        var res = await CreateAsync(await AdminAsync(), header, RollupField("Count", "int", line, "Teacher", "count", sensitivity: "Sensitive"));

        res.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(res));
    }

    [Fact]
    public async Task Create_refuses_a_saver_who_may_not_read_the_via_field()
    {
        var (header, line) = await LineAsync(teacher: SensitivityLevel.Sensitive);
        var saver = R.Client(_fixture, await R.UserAsync(_fixture, SystemCapabilities.ManageContentTypes, line));

        var res = await CreateAsync(saver, header, RollupField("Count", "int", line, "Teacher", "count", sensitivity: "Sensitive"));

        await AssertRefusedAsync(res, $"You may not read '{line}.Teacher'");
        await AssertNotStoredAsync(header);
    }

    [Fact]
    public async Task Create_accepts_a_sensitive_count_from_a_saver_who_reads_the_via_field()
    {
        var (header, line) = await LineAsync(teacher: SensitivityLevel.Sensitive);
        var token = await R.ReaderWithAsync(_fixture, [SystemCapabilities.ManageContentTypes, SystemCapabilities.ViewSensitive], (line, null));

        var res = await CreateAsync(R.Client(_fixture, token), header, RollupField("Count", "int", line, "Teacher", "count", sensitivity: "Sensitive"));

        res.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(res));
    }

    // Integration: the window field a rollup counts by.

    private static object WindowedRollup(string line, string key, string word, string sensitivity = "Public") =>
        new
        {
            name = "Recent", type = "int", sensitivity,
            rollup = new { from = line, via = "Teacher", fn = "count", where = new Dictionary<string, string> { [key] = word } },
        };

    [Fact]
    public async Task Create_saves_a_window_on_createdat_and_on_a_date_field_of_the_line_type()
    {
        var (header, line) = await LineAsync(extra: R.Plain("Day", "date"));
        var (other, otherLine) = await LineAsync(extra: R.Plain("Day", "datetime"));
        var admin = await AdminAsync();

        var made = await CreateAsync(admin, header, WindowedRollup(line, "createdAt", "thisMonth"));
        var dated = await CreateAsync(admin, other, WindowedRollup(otherLine, "Day", "last7Days"));

        made.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(made));
        dated.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(dated));
        (await StoredFieldAsync(header, "Recent"))!.Rollup!.Where.Should().Equal(new Dictionary<string, string> { ["createdAt"] = "thisMonth" });
        (await StoredFieldAsync(other, "Recent"))!.Rollup!.Where.Should().ContainKey("Day");
    }

    [Fact]
    public async Task Create_refuses_a_window_on_a_field_the_line_type_does_not_declare()
    {
        var (header, line) = await LineAsync();

        var res = await CreateAsync(await AdminAsync(), header, WindowedRollup(line, "Day", "today"));

        await AssertRefusedAsync(res, "windows on 'Day', which");
        await AssertNotStoredAsync(header);
    }

    [Fact]
    public async Task Create_refuses_a_window_on_a_field_that_is_not_a_date()
    {
        var (header, line) = await LineAsync();

        var res = await CreateAsync(await AdminAsync(), header, WindowedRollup(line, "Hours", "today"));

        await AssertRefusedAsync(res, "A window takes a date or datetime field");
    }

    [Fact]
    public async Task Create_refuses_a_rollup_more_widely_readable_than_its_window_field()
    {
        var (header, line) = await LineAsync(extra: R.Plain("Day", "date", SensitivityLevel.Sensitive));

        var res = await CreateAsync(await AdminAsync(), header, WindowedRollup(line, "Day", "today"));

        await AssertRefusedAsync(res, "may not be more widely readable than the field that decides");
    }

    [Fact]
    public async Task Create_refuses_a_saver_who_may_not_read_the_window_field()
    {
        var (header, line) = await LineAsync(extra: R.Plain("Day", "date"));
        var saver = R.Client(_fixture, await R.ReaderAsync(_fixture, SystemCapabilities.ManageContentTypes, (line, ["Teacher"])));

        var res = await CreateAsync(saver, header, WindowedRollup(line, "Day", "today"));

        await AssertRefusedAsync(res, $"You may not read '{line}.Day'");
        await AssertNotStoredAsync(header);
    }

    [Fact]
    public async Task Create_accepts_a_window_on_createdat_from_a_saver_who_reads_only_the_via_field()
    {
        var (header, line) = await LineAsync();
        var saver = R.Client(_fixture, await R.ReaderAsync(_fixture, SystemCapabilities.ManageContentTypes, (line, ["Teacher"])));

        var res = await CreateAsync(saver, header, WindowedRollup(line, "createdAt", "today"));

        res.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(res));
    }

    private async Task<(string Header, string Line)> WindowedPairAsync()
    {
        var (header, line) = await LineAsync(extra: R.Plain("Day", "date"));
        await StoreAsync(Type(header, R.Plain("Name", "string"), R.Rollup("Recent", "int", line, "Teacher", "count", windowKey: "Day", window: "last7Days")));
        return (header, line);
    }

    [Fact]
    public async Task Removing_renaming_or_retyping_the_window_field_is_a_409_naming_the_rollup()
    {
        var (header, line) = await WindowedPairAsync();
        var admin = await AdminAsync();

        var remove = await admin.PostAsJsonAsync($"/api/content-types/{line}/fields/Day/change", new { remove = true, allowDestructive = true }, Ct);
        await AssertRefusedAsync(remove, $"This would break the rollup {header}.", HttpStatusCode.Conflict);

        var rename = await admin.PostAsJsonAsync($"/api/content-types/{line}/fields/Day/change", new { newName = "Day2" }, Ct);
        await AssertRefusedAsync(rename, $"This would break the rollup {header}.", HttpStatusCode.Conflict);

        var retype = await admin.PostAsJsonAsync($"/api/content-types/{line}/fields/Day/change", new { toType = "string", allowDestructive = true }, Ct);
        await AssertRefusedAsync(retype, $"This would break the rollup {header}.", HttpStatusCode.Conflict);

        (await StoredFieldAsync(line, "Day"))!.Type.Should().Be("date");
    }

    [Fact]
    public async Task Raising_the_window_field_above_the_rollups_level_is_a_409()
    {
        var (header, line) = await WindowedPairAsync();

        var raised = await (await AdminAsync()).PutAsJsonAsync($"/api/content-types/{line}/fields/Day/sensitivity", new { sensitivity = "Sensitive" }, Ct);

        await AssertRefusedAsync(raised, header, HttpStatusCode.Conflict);
        (await StoredFieldAsync(line, "Day"))!.Sensitivity.Should().Be(SensitivityLevel.Public);
    }

    [Fact]
    public async Task A_window_on_createdat_does_not_hold_back_a_change_to_any_field_of_the_line_type()
    {
        var (header, line) = await LineAsync(extra: R.Plain("Day", "date"));
        await StoreAsync(Type(header, R.Plain("Name", "string"), R.Rollup("Recent", "int", line, "Teacher", "count", windowKey: "createdAt", window: "today")));

        var res = await (await AdminAsync()).PostAsJsonAsync($"/api/content-types/{line}/fields/Day/change", new { remove = true, allowDestructive = true }, Ct);

        res.IsSuccessStatusCode.Should().BeTrue(await BodyAsync(res));
    }

    // Integration: a field added to a header that exists.

    [Fact]
    public async Task AddField_saves_a_rollup_and_refuses_a_bad_one()
    {
        var (header, line) = await LineAsync();
        await StoreAsync(Type(header, R.Plain("Name", "string")));
        var admin = await AdminAsync();

        var bad = await admin.PostAsJsonAsync(
            $"/api/content-types/{header}/fields",
            new { fieldName = "Count", type = "int", rollup = new { from = line, via = "Nobody", fn = "count" } }, Ct);
        await AssertRefusedAsync(bad, "does not declare");
        (await StoredFieldAsync(header, "Count")).Should().BeNull();

        var good = await admin.PostAsJsonAsync(
            $"/api/content-types/{header}/fields",
            new { fieldName = "Count", type = "int", rollup = new { from = line, via = "Teacher", fn = "count" } }, Ct);
        good.IsSuccessStatusCode.Should().BeTrue(await BodyAsync(good));
        (await StoredFieldAsync(header, "Count"))!.Rollup!.Via.Should().Be("Teacher");
    }

    [Fact]
    public async Task AddField_refuses_a_saver_whose_read_rule_does_not_show_the_via_field()
    {
        var (header, line) = await LineAsync();
        await StoreAsync(Type(header, R.Plain("Name", "string")));
        var limited = R.Client(_fixture, await R.ReaderAsync(_fixture, SystemCapabilities.ManageContentTypes, (line, ["Hours"])));
        var whole = R.Client(_fixture, await R.ReaderAsync(_fixture, SystemCapabilities.ManageContentTypes, (line, null)));
        var body = new { fieldName = "Count", type = "int", rollup = new { from = line, via = "Teacher", fn = "count" } };

        var refused = await limited.PostAsJsonAsync($"/api/content-types/{header}/fields", body, Ct);
        await AssertRefusedAsync(refused, $"You may not read '{line}.Teacher'");
        (await StoredFieldAsync(header, "Count")).Should().BeNull();

        var accepted = await whole.PostAsJsonAsync($"/api/content-types/{header}/fields", body, Ct);
        accepted.IsSuccessStatusCode.Should().BeTrue(await BodyAsync(accepted));
    }

    [Fact]
    public async Task AddField_refuses_a_saver_who_may_not_read_the_summed_type()
    {
        var (header, line) = await LineAsync();
        await StoreAsync(Type(header, R.Plain("Name", "string")));
        var blind = R.Client(_fixture, await R.ReaderAsync(_fixture, SystemCapabilities.ManageContentTypes));

        var res = await blind.PostAsJsonAsync(
            $"/api/content-types/{header}/fields",
            new { fieldName = "Count", type = "int", rollup = new { from = line, via = "Teacher", fn = "count" } }, Ct);

        await AssertRefusedAsync(res, $"You may not read '{line}'");
    }

    // Integration: who the saver is, checked where the saver is a person, a principal key or nobody.

    private async Task<List<string>> CallerErrorsAsync(ClaimsPrincipal principal, string header, string line)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var caller = new RollupCaller(
            principal, scope.ServiceProvider.GetRequiredService<ISensitivityService>(), scope.ServiceProvider.GetRequiredService<IPermissionResolver>());
        var definition = Type(header, R.Plain("Name", "string"), R.Rollup("Count", "int", line, "Teacher", "count"));

        return await RollupFields.DefinitionErrorsAsync(session, definition, null, caller, Ct);
    }

    private async Task<Guid> PrincipalAsync(string? readsType)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"dbrole_{Guid.NewGuid():N}",
            Permissions = readsType is null ? [] :
            [
                new ContentTypePermission
                {
                    ContentTypeSlug = readsType,
                    Read = new PermissionRule { Enabled = true },
                    Create = new PermissionRule { Enabled = false },
                    Update = new PermissionRule { Enabled = false },
                    Delete = new PermissionRule { Enabled = false },
                },
            ],
        };
        var principal = new ServicePrincipal { Name = "p" + Guid.NewGuid().ToString("n")[..8], RoleIds = [role.Id] };
        session.Store(role);
        session.Store(principal);
        await session.SaveChangesAsync(Ct);
        return principal.Id;
    }

    private static ClaimsPrincipal Keyed(Guid principalId) =>
        new(new ClaimsIdentity([new Claim("principal_id", principalId.ToString())], "test"));

    [Fact]
    public async Task A_principal_key_that_may_not_read_the_summed_type_is_refused()
    {
        var (header, line) = await LineAsync();
        var principal = await PrincipalAsync(readsType: null);

        var errors = await CallerErrorsAsync(Keyed(principal), header, line);

        errors.Should().Contain(e => e.Contains($"You may not read '{line}'"));
    }

    [Fact]
    public async Task A_principal_key_that_may_read_the_summed_type_is_accepted()
    {
        var (header, line) = await LineAsync();
        var principal = await PrincipalAsync(readsType: line);

        (await CallerErrorsAsync(Keyed(principal), header, line)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_caller_that_resolves_to_no_user_is_refused_and_not_skipped()
    {
        var (header, line) = await LineAsync();
        var nobody = new ClaimsPrincipal(new ClaimsIdentity([new Claim("UserId", Guid.NewGuid().ToString())], "test"));
        var gone = Keyed(Guid.NewGuid());

        (await CallerErrorsAsync(nobody, header, line)).Should().Contain(e => e.Contains("could not be identified"));
        (await CallerErrorsAsync(gone, header, line)).Should().Contain(e => e.Contains("could not be identified"));
        (await CallerErrorsAsync(new ClaimsPrincipal(new ClaimsIdentity()), header, line)).Should().Contain(e => e.Contains("could not be identified"));
    }

    // Integration: the break check on every other writer of a counted type.

    [Fact]
    public async Task The_currency_endpoint_refuses_a_change_that_breaks_a_money_rollup_and_stores_nothing()
    {
        var (header, line) = Names();
        var amount = R.Plain("Amount", "money");
        amount.Currency = "USD";
        var total = R.Rollup("Total", "money", line, "Teacher", "sum", "Amount");
        total.Currency = "USD";
        await StoreAsync(Type(line, R.Reference("Teacher", header), amount), Type(header, R.Plain("Name", "string"), total));

        var res = await (await AdminAsync()).PutAsJsonAsync(
            $"/api/content-types/{line}/fields/Amount/currency", new { currency = "EUR", force = true }, Ct);

        await AssertRefusedAsync(res, "This would break the rollup", HttpStatusCode.Conflict);
        (await StoredFieldAsync(line, "Amount"))!.Currency.Should().Be("USD");
    }

    [Fact]
    public async Task The_lines_endpoint_refuses_a_rollup_as_the_lines_total_and_stores_nothing()
    {
        var (header, line) = await StoredPairAsync();

        var res = await (await AdminAsync()).PutAsJsonAsync(
            $"/api/content-types/{header}/lines",
            new { lines = new { contentType = line, parentField = "Teacher", totalField = "Total", lineAmountField = "Hours" } }, Ct);

        await AssertRefusedAsync(res, "is a rollup, so it cannot be the lines total");
        (await Probe.StoredTypeAsync(_fixture, header)).Lines.Should().BeNull();
    }

    [Fact]
    public async Task A_bundle_import_that_breaks_a_rollup_is_refused_and_stores_nothing()
    {
        var (header, line) = await StoredPairAsync();
        var bundle = new
        {
            contentTypes = new[]
            {
                new
                {
                    name = line, displayName = line, branchScoped = true,
                    fields = new object[]
                    {
                        new { name = "Teacher", type = "reference", referenceType = header },
                        new { name = "Hours", type = "decimal" },
                    },
                },
            },
            contents = Array.Empty<object>(),
        };

        var res = await (await AdminAsync()).PostAsJsonAsync("/api/portability/import", bundle, Ct);

        await AssertRefusedAsync(res, "is branch scoped");
        (await Probe.StoredTypeAsync(_fixture, line)).BranchScoped.Should().BeFalse();
    }

    // Integration: config as code.

    private static object ConfigOf(string header, params object[] fields) => new
    {
        document = new
        {
            version = 1,
            contentTypes = new Dictionary<string, object>
            {
                [header] = new { name = header, displayName = header, fields = WithName(fields) },
            },
        },
        allowDestructive = true,
    };

    [Fact]
    public async Task A_config_apply_refuses_a_rollup_the_stored_types_do_not_support()
    {
        var (header, line) = await LineAsync();
        await StoreAsync(Type(header, R.Plain("Name", "string")));

        var res = await (await AdminAsync()).PostAsJsonAsync(
            "/api/config/apply", ConfigOf(header, RollupField("Count", "int", line, "Nobody", "count")), Ct);

        await AssertRefusedAsync(res, "does not declare");
        (await StoredFieldAsync(header, "Count")).Should().BeNull();
    }

    [Fact]
    public async Task A_config_apply_saves_a_rollup_the_stored_types_support()
    {
        var (header, line) = await LineAsync();
        await StoreAsync(Type(header, R.Plain("Name", "string")));

        var res = await (await AdminAsync()).PostAsJsonAsync(
            "/api/config/apply", ConfigOf(header, RollupField("Count", "int", line, "Teacher", "count")), Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(res));
        (await StoredFieldAsync(header, "Count"))!.Rollup!.Fn.Should().Be("count");
    }

    [Fact]
    public async Task A_config_apply_that_lowers_a_rollup_is_refused_and_leaves_the_type_alone()
    {
        var (header, line) = await StoredPairAsync(level: SensitivityLevel.Sensitive);

        var res = await (await AdminAsync()).PostAsJsonAsync(
            "/api/config/apply",
            ConfigOf(header, RollupField("Count", "int", line, "Teacher", "count", sensitivity: "Public")), Ct);

        await AssertRefusedAsync(res, "does not change who may read a field");
        (await StoredFieldAsync(header, "Count"))!.Sensitivity.Should().Be(SensitivityLevel.Sensitive);
    }

    [Fact]
    public async Task A_config_apply_that_keeps_a_rollups_level_is_not_refused_for_it()
    {
        var (header, line) = await StoredPairAsync(level: SensitivityLevel.Sensitive);

        var res = await (await AdminAsync()).PostAsJsonAsync(
            "/api/config/apply",
            ConfigOf(header, RollupField("Count", "int", line, "Teacher", "count", sensitivity: "Sensitive")), Ct);

        (await BodyAsync(res)).Should().NotContain("does not change who may read a field");
    }

    // Integration: a change to the type a rollup summarises.

    [Fact]
    public async Task Removing_or_renaming_the_via_or_the_summed_field_is_a_409_naming_the_rollup()
    {
        var (header, line) = await StoredPairAsync();
        var admin = await AdminAsync();

        foreach (var field in new[] { "Teacher", "Hours" })
        {
            var remove = await admin.PostAsJsonAsync($"/api/content-types/{line}/fields/{field}/change", new { remove = true, allowDestructive = true }, Ct);
            await AssertRefusedAsync(remove, $"This would break the rollup {header}.", HttpStatusCode.Conflict);

            var rename = await admin.PostAsJsonAsync($"/api/content-types/{line}/fields/{field}/change", new { newName = field + "2" }, Ct);
            await AssertRefusedAsync(rename, $"This would break the rollup {header}.", HttpStatusCode.Conflict);
        }

        var stored = await Probe.StoredTypeAsync(_fixture, line);
        stored.Fields.Select(f => f.Name).Should().BeEquivalentTo("Teacher", "Hours");
    }

    [Fact]
    public async Task Changing_the_type_of_the_summed_field_is_a_409()
    {
        var (header, line) = await StoredPairAsync();

        var res = await (await AdminAsync()).PostAsJsonAsync(
            $"/api/content-types/{line}/fields/Hours/change", new { toType = "string", allowDestructive = true }, Ct);

        await AssertRefusedAsync(res, $"This would break the rollup {header}.", HttpStatusCode.Conflict);
        (await StoredFieldAsync(line, "Hours"))!.Type.Should().Be("decimal");
    }

    [Fact]
    public async Task A_type_a_rollup_summarises_cannot_be_deleted()
    {
        var (header, line) = await StoredPairAsync();
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var errors = await RollupFields.BreaksAsync(session, line, null, Ct);

        errors.Should().ContainSingle(e => e.Contains($"{header}.Count"));
        errors.Should().ContainSingle(e => e.Contains($"{header}.Total"));
        errors.Should().OnlyContain(e => e.Contains("cannot be deleted"));
    }

    [Fact]
    public async Task Raising_the_via_or_the_summed_field_above_the_rollups_level_is_a_409()
    {
        var (header, line) = await StoredPairAsync();
        var admin = await AdminAsync();

        foreach (var field in new[] { "Teacher", "Hours" })
        {
            var raised = await admin.PutAsJsonAsync($"/api/content-types/{line}/fields/{field}/sensitivity", new { sensitivity = "Sensitive" }, Ct);

            await AssertRefusedAsync(raised, header, HttpStatusCode.Conflict);
            (await StoredFieldAsync(line, field))!.Sensitivity.Should().Be(SensitivityLevel.Public);
        }
    }

    [Fact]
    public async Task Raising_the_via_to_the_rollups_level_is_accepted()
    {
        var (_, line) = await StoredPairAsync(level: SensitivityLevel.Sensitive);

        var raised = await (await AdminAsync()).PutAsJsonAsync(
            $"/api/content-types/{line}/fields/Teacher/sensitivity", new { sensitivity = "Sensitive" }, Ct);

        raised.IsSuccessStatusCode.Should().BeTrue(await BodyAsync(raised));
    }

    [Fact]
    public async Task Lowering_a_rollup_with_the_sensitivity_endpoint_is_not_refused_as_a_definition_write()
    {
        var (header, _) = await StoredPairAsync(level: SensitivityLevel.Sensitive);

        var lowered = await (await AdminAsync()).PutAsJsonAsync(
            $"/api/content-types/{header}/fields/Count/sensitivity", new { sensitivity = "Public", acknowledgeDisclosure = true }, Ct);

        lowered.IsSuccessStatusCode.Should().BeTrue(await BodyAsync(lowered));
    }
}

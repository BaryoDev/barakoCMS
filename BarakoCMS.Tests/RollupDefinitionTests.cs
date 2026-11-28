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
    public async Task A_config_apply_whose_header_has_a_text_line_amount_is_refused_on_that_type_and_stores_nothing()
    {
        var (header, line) = await LineAsync(SensitivityLevel.Public, SensitivityLevel.Public, R.Plain("Memo", "string"));

        var res = await (await AdminAsync()).PostAsJsonAsync(
            "/api/config/apply", ConfigOfTypes((header, HeaderWithLines(header, line, "decimal", "Memo"))), Ct);

        await AssertRefusedAsync(res, $"'{LinesTotal}' on '{header}' and 'Memo' on '{line}' must both be number fields.");
        await AssertNotStoredAsync(header);
    }

    // Integration: config as code.

    private const string LinesTotal = "Total";

    /// <summary>A header whose lines are <paramref name="line"/>, summed into Total, with the line amount in <paramref name="amount"/>.</summary>
    private static object HeaderWithLines(string header, string line, string totalType, string amount) => new
    {
        name = header, displayName = header,
        lines = new { contentType = line, parentField = "Teacher", totalField = LinesTotal, lineAmountField = amount },
        fields = new object[] { new { name = "Name", type = "string" }, new { name = LinesTotal, type = totalType } },
    };

    private static object ConfigOfTypes(params (string Key, object Type)[] types) => new
    {
        document = new { version = 1, contentTypes = types.ToDictionary(t => t.Key, t => t.Type) },
        allowDestructive = true,
    };

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
    public async Task A_config_apply_whose_header_has_a_text_lines_total_is_refused_and_stores_nothing()
    {
        var (header, line) = await LineAsync();

        var res = await (await AdminAsync()).PostAsJsonAsync(
            "/api/config/apply", ConfigOfTypes((header, HeaderWithLines(header, line, "string", "Hours"))), Ct);

        await AssertRefusedAsync(res, $"'{LinesTotal}' on '{header}' and 'Hours' on '{line}' must both be number fields.");
        await AssertNotStoredAsync(header);
    }

    [Fact]
    public async Task A_config_apply_that_brings_a_header_and_its_line_type_together_is_not_refused_for_the_lines()
    {
        var (header, line) = Names();
        var lineType = new
        {
            name = line, displayName = line,
            fields = new object[]
            {
                new { name = "Teacher", type = "reference", referenceType = header },
                new { name = "Hours", type = "decimal" },
            },
        };

        var res = await (await AdminAsync()).PostAsJsonAsync(
            "/api/config/apply", ConfigOfTypes((header, HeaderWithLines(header, line, "decimal", "Hours")), (line, lineType)), Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(res));
        (await Probe.StoredTypeAsync(_fixture, header)).Name.Should().Be(header);
        (await Probe.StoredTypeAsync(_fixture, line)).Name.Should().Be(line);
    }

    // Lines are stored by config apply and import, in the spelling the lines route stores.

    /// <summary>A header and its line type, the lines naming everything in another case than the types hold it.</summary>
    private static (object Header, object Line) MiscasedPair(string header, string line) => (
        new
        {
            name = header, displayName = header,
            lines = new { contentType = line.ToUpperInvariant(), parentField = "teacher", totalField = "total", lineAmountField = "hours" },
            fields = new object[] { new { name = "Name", type = "string" }, new { name = LinesTotal, type = "decimal" } },
        },
        new
        {
            name = line, displayName = line,
            fields = new object[]
            {
                new { name = "Teacher", type = "reference", referenceType = header },
                new { name = "Hours", type = "decimal" },
            },
        });

    private async Task AssertStoredLinesAsync(string header, string line)
    {
        var lines = (await Probe.StoredTypeAsync(_fixture, header)).Lines;
        lines.Should().NotBeNull("the checks passed, so the lines are stored");
        (lines!.ContentType, lines.ParentField, lines.TotalField, lines.LineAmountField)
            .Should().Be((line, "Teacher", LinesTotal, "Hours"));
    }

    /// <summary>A stored line type and a header with lines, as the lines route stores them.</summary>
    private async Task<(string Header, string Line)> StoredWithLinesAsync()
    {
        var (header, line) = await LineAsync();
        var type = Type(header, R.Plain("Name", "string"), R.Plain(LinesTotal, "decimal"));
        type.Lines = new LinesDefinition { ContentType = line, ParentField = "Teacher", TotalField = LinesTotal, LineAmountField = "Hours" };
        await StoreAsync(type);
        return (header, line);
    }

    private async Task DeleteTypesAsync(params string[] names)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        foreach (var type in await session.Query<ContentTypeDefinition>().Where(d => names.Contains(d.Name)).ToListAsync(Ct))
            session.Delete(type);
        await session.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task A_config_apply_stores_the_lines_it_passed_in_the_stored_spelling()
    {
        var (header, line) = Names();
        var (headerType, lineType) = MiscasedPair(header, line);

        var res = await (await AdminAsync()).PostAsJsonAsync(
            "/api/config/apply", ConfigOfTypes((header, headerType), (line, lineType)), Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(res));
        await AssertStoredLinesAsync(header, line);
    }

    [Fact]
    public async Task A_second_config_apply_without_lines_keeps_the_stored_lines()
    {
        var (header, line) = Names();
        var (headerType, lineType) = MiscasedPair(header, line);
        var admin = await AdminAsync();
        (await admin.PostAsJsonAsync("/api/config/apply", ConfigOfTypes((header, headerType), (line, lineType)), Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var withoutLines = new
        {
            name = header, displayName = header,
            fields = new object[] { new { name = "Name", type = "string" }, new { name = LinesTotal, type = "decimal" } },
        };
        var res = await admin.PostAsJsonAsync("/api/config/apply", ConfigOfTypes((header, withoutLines)), Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(res));
        await AssertStoredLinesAsync(header, line);
    }

    [Fact]
    public async Task A_config_export_then_apply_into_an_empty_tenant_keeps_the_lines()
    {
        var (header, line) = await StoredWithLinesAsync();
        var admin = await AdminAsync();
        var export = await (await admin.GetAsync("/api/config/export", Ct)).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Ct);
        var types = export.GetProperty("contentTypes");
        var documents = new[] { (header, types.GetProperty(header)), (line, types.GetProperty(line)) };
        await DeleteTypesAsync(header, line);

        var res = await admin.PostAsJsonAsync("/api/config/apply", ConfigOfTypes(documents.Select(d => (d.Item1, (object)d.Item2)).ToArray()), Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(res));
        await AssertStoredLinesAsync(header, line);
    }

    [Fact]
    public async Task A_second_config_plan_of_the_same_miscased_lines_reports_no_change()
    {
        var (header, line) = Names();
        var (headerType, lineType) = MiscasedPair(header, line);
        var admin = await AdminAsync();
        var document = ConfigOfTypes((header, headerType), (line, lineType));
        (await admin.PostAsJsonAsync("/api/config/apply", document, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var plan = await admin.PostAsJsonAsync("/api/config/plan", document, Ct);

        var body = await BodyAsync(plan);
        plan.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var changes = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("sections").EnumerateArray()
            .SelectMany(s => s.GetProperty("changes").EnumerateArray()).Select(c => c.GetProperty("key").GetString()).ToList();
        changes.Should().NotContain(header, "the stored spelling of the lines is the same lines");
    }

    [Fact]
    public async Task A_config_apply_adds_lines_to_a_stored_header_that_has_none_in_the_stored_spelling()
    {
        var (header, line) = await LineAsync();
        await StoreAsync(Type(header, R.Plain("Name", "string"), R.Plain(LinesTotal, "decimal")));
        var (headerType, _) = MiscasedPair(header, line);

        var res = await (await AdminAsync()).PostAsJsonAsync("/api/config/apply", ConfigOfTypes((header, headerType)), Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(res));
        await AssertStoredLinesAsync(header, line);
    }

    // Lines are checked as they will be stored, on every path.

    private static object HeaderDoc(string header, string? lineType, bool total = true, string lineName = "Teacher") => new
    {
        name = header, displayName = header,
        lines = lineType is null ? null : new { contentType = lineType, parentField = lineName, totalField = LinesTotal, lineAmountField = "Hours" },
        fields = total
            ? new object[] { new { name = "Name", type = "string" }, new { name = LinesTotal, type = "decimal" } }
            : [new { name = "Name", type = "string" }],
    };

    private async Task AssertStillStoredWithLinesAsync(string header, string line)
    {
        var stored = await Probe.StoredTypeAsync(_fixture, header);
        stored.Fields.Should().Contain(f => f.Name == LinesTotal, "a refused save changes nothing");
        stored.Lines.Should().NotBeNull();
        stored.Lines!.ContentType.Should().Be(line);
    }

    [Fact]
    public async Task A_config_apply_resolves_a_line_type_named_in_any_spelling_the_route_accepts()
    {
        var header = Names().Header;
        var line = "ord-line-" + Guid.NewGuid().ToString("n")[..8];
        await StoreAsync(Type(line, R.Reference("Teacher", header), R.Plain("Hours", "decimal")));

        var res = await (await AdminAsync()).PostAsJsonAsync(
            "/api/config/apply", ConfigOfTypes((header, HeaderDoc(header, " " + line.Replace('-', ' ').ToUpperInvariant() + " "))), Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(res));
        await AssertStoredLinesAsync(header, line);
    }

    [Fact]
    public async Task A_config_apply_that_omits_lines_and_drops_the_stored_lines_total_is_refused()
    {
        var (header, line) = await StoredWithLinesAsync();

        var res = await (await AdminAsync()).PostAsJsonAsync(
            "/api/config/apply", ConfigOfTypes((header, HeaderDoc(header, null, total: false))), Ct);

        await AssertRefusedAsync(res, $"'{LinesTotal}' on '{header}' and 'Hours' on '{line}' must both be number fields.");
        await AssertStillStoredWithLinesAsync(header, line);
    }

    [Fact]
    public async Task A_config_apply_that_redeclares_a_line_type_without_its_parent_field_is_refused_while_a_header_names_it()
    {
        var (header, line) = await StoredWithLinesAsync();
        var redeclared = new { name = line, displayName = line, fields = new object[] { new { name = "Hours", type = "decimal" } } };

        var res = await (await AdminAsync()).PostAsJsonAsync("/api/config/apply", ConfigOfTypes((line, redeclared)), Ct);

        await AssertRefusedAsync(res, $"This would break the lines of '{header}'. Line type '{line}' has no field 'Teacher'.");
        (await Probe.StoredTypeAsync(_fixture, line)).Fields.Should().Contain(f => f.Name == "Teacher");
    }

    [Fact]
    public async Task A_config_apply_that_gives_a_stored_line_type_that_keeps_its_lines_a_header_is_refused()
    {
        var (header, line) = Names();
        var deeper = "rd" + Guid.NewGuid().ToString("n")[..10];
        await StoreAsync(Type(deeper, R.Reference("Parent", line)));
        var child = Type(line, R.Reference("Teacher", header), R.Plain("Hours", "decimal"));
        child.Lines = new LinesDefinition { ContentType = deeper, ParentField = "Parent" };
        await StoreAsync(child);
        var childWithoutLines = new
        {
            name = line, displayName = line,
            fields = new object[] { new { name = "Teacher", type = "reference", referenceType = header }, new { name = "Hours", type = "decimal" } },
        };

        var res = await (await AdminAsync()).PostAsJsonAsync(
            "/api/config/apply", ConfigOfTypes((header, HeaderDoc(header, line)), (line, childWithoutLines)), Ct);

        await AssertRefusedAsync(res, $"Line type '{line}' has lines of its own. Lines are one level deep.");
        await AssertNotStoredAsync(header);
    }

    [Fact]
    public async Task The_lines_route_refuses_lines_on_a_type_that_is_already_a_line_type()
    {
        var (header, line) = await StoredWithLinesAsync();
        var deeper = "rd" + Guid.NewGuid().ToString("n")[..10];
        await StoreAsync(Type(deeper, R.Reference("Parent", line)));

        var res = await (await AdminAsync()).PutAsJsonAsync(
            $"/api/content-types/{line}/lines", new { lines = new { contentType = deeper, parentField = "Parent" } }, Ct);

        await AssertRefusedAsync(res, $"'{line}' is the line type of '{header}', so it cannot have lines of its own.");
        (await Probe.StoredTypeAsync(_fixture, line)).Lines.Should().BeNull();
    }

    [Fact]
    public async Task A_config_apply_that_gives_a_type_that_is_a_line_type_lines_of_its_own_is_refused()
    {
        var (header, line) = await StoredWithLinesAsync();
        var deeper = "rd" + Guid.NewGuid().ToString("n")[..10];
        await StoreAsync(Type(deeper, R.Reference("Parent", line)));
        var withLines = new
        {
            name = line, displayName = line,
            lines = new { contentType = deeper, parentField = "Parent" },
            fields = new object[] { new { name = "Teacher", type = "reference", referenceType = header }, new { name = "Hours", type = "decimal" } },
        };

        var res = await (await AdminAsync()).PostAsJsonAsync("/api/config/apply", ConfigOfTypes((line, withLines)), Ct);

        await AssertRefusedAsync(res, $"'{line}' is the line type of '{header}', so it cannot have lines of its own.");
    }

    [Fact]
    public async Task The_lines_route_refuses_a_header_that_is_its_own_line_type()
    {
        var (header, _) = Names();
        await StoreAsync(Type(header, R.Plain("Name", "string")));

        var res = await (await AdminAsync()).PutAsJsonAsync(
            $"/api/content-types/{header}/lines", new { lines = new { contentType = header, parentField = "Name" } }, Ct);

        await AssertRefusedAsync(res, "A type cannot be its own line type.");
    }

    [Fact]
    public async Task A_config_apply_of_a_header_that_is_its_own_line_type_is_refused()
    {
        var (header, _) = Names();

        var res = await (await AdminAsync()).PostAsJsonAsync(
            "/api/config/apply", ConfigOfTypes((header, HeaderDoc(header, header, lineName: "Name"))), Ct);

        await AssertRefusedAsync(res, "A type cannot be its own line type.");
        await AssertNotStoredAsync(header);
    }

    [Fact]
    public async Task A_config_apply_of_an_unrelated_type_leaves_a_stored_header_whose_lines_do_not_hold_alone()
    {
        var (header, line) = await LineAsync();
        var broken = Type(header, R.Plain("Name", "string"), R.Plain(LinesTotal, "string"));
        broken.Lines = new LinesDefinition { ContentType = line, ParentField = "teacher", TotalField = "total", LineAmountField = "hours" };
        await StoreAsync(broken);
        var unrelated = Names().Header;

        var res = await (await AdminAsync()).PostAsJsonAsync(
            "/api/config/apply", ConfigOfTypes((unrelated, new { name = unrelated, displayName = unrelated, fields = new object[] { new { name = "Name", type = "string" } } })), Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(res));
        var lines = (await Probe.StoredTypeAsync(_fixture, header)).Lines!;
        (lines.ParentField, lines.TotalField, lines.LineAmountField).Should().Be(("teacher", "total", "hours"),
            "lines that do not hold are not respelled, and an unrelated apply does not refuse them");
    }

    // Round 6: one normalized match, only what the save introduces, and one lock order.

    /// <summary>A stored hyphenated header with lines, its line type, and a spelling of the header that normalizes to it.</summary>
    private async Task<(string Header, string Line, string Spelled)> StoredHyphenatedAsync()
    {
        var id = Guid.NewGuid().ToString("n")[..8];
        var (header, line) = ($"so-{id}", $"sol-{id}");
        await StoreAsync(Type(line, R.Reference("Teacher", header), R.Plain("Hours", "decimal")));
        var type = Type(header, R.Plain("Name", "string"), R.Plain(LinesTotal, "decimal"));
        type.Lines = new LinesDefinition { ContentType = line, ParentField = "Teacher", TotalField = LinesTotal, LineAmountField = "Hours" };
        await StoreAsync(type);
        return (header, line, "So " + id);
    }

    [Fact]
    public async Task A_config_apply_keyed_with_spaces_over_a_stored_header_checks_its_lines_and_refuses_a_text_total()
    {
        var (header, line, spelled) = await StoredHyphenatedAsync();

        var res = await (await AdminAsync()).PostAsJsonAsync(
            "/api/config/apply", ConfigOfTypes((spelled, HeaderWithLines(spelled, line, "string", "Hours"))), Ct);

        await AssertRefusedAsync(res, $"'{LinesTotal}' on '{header}' and 'Hours' on '{line}' must both be number fields.");
        (await StoredFieldAsync(header, LinesTotal))!.Type.Should().Be("decimal", "a refused apply stores nothing");
    }

    [Fact]
    public async Task A_config_apply_creating_a_header_keyed_with_spaces_accepts_a_line_type_that_references_its_normalized_name()
    {
        var id = Guid.NewGuid().ToString("n")[..8];
        var (header, line, spelled) = ($"ord-{id}", $"ordl-{id}", "Ord " + id);
        await StoreAsync(Type(line, R.Reference("Teacher", header), R.Plain("Hours", "decimal")));

        var res = await (await AdminAsync()).PostAsJsonAsync(
            "/api/config/apply", ConfigOfTypes((spelled, HeaderWithLines(spelled, line, "decimal", "Hours"))), Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(res));
        await AssertStoredLinesAsync(header, line);
    }

    [Fact]
    public async Task A_config_apply_keying_one_type_twice_is_refused_and_stores_nothing()
    {
        var id = Guid.NewGuid().ToString("n")[..8];
        object Entry(string name) => new { name, displayName = name, fields = new object[] { new { name = "Name", type = "string" } } };

        var res = await (await AdminAsync()).PostAsJsonAsync(
            "/api/config/apply", ConfigOfTypes(($"dup-x-{id}", Entry($"dup-x-{id}")), ($"Dup-X-{id}", Entry($"Dup-X-{id}"))), Ct);

        var body = await BodyAsync(res);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain($"name the same content type 'dup-x-{id}'.");
        await AssertNotStoredAsync($"dup-x-{id}");
        await AssertNotStoredAsync($"Dup-X-{id}");
    }

    [Fact]
    public async Task A_config_apply_of_a_stored_chain_as_exported_is_not_refused_for_the_fault_it_already_had()
    {
        var id = Guid.NewGuid().ToString("n")[..8];
        var (header, line, deeper) = ($"ch-{id}", $"cl-{id}", $"cd-{id}");
        await StoreAsync(Type(deeper, R.Reference("Parent", line)));
        var child = Type(line, R.Reference("Teacher", header), R.Plain("Hours", "decimal"));
        child.Lines = new LinesDefinition { ContentType = deeper, ParentField = "Parent" };
        var top = Type(header, R.Plain("Name", "string"), R.Plain(LinesTotal, "decimal"));
        top.Lines = new LinesDefinition { ContentType = line, ParentField = "Teacher", TotalField = LinesTotal, LineAmountField = "Hours" };
        await StoreAsync(child, top);
        var childDoc = new
        {
            name = line, displayName = line,
            lines = new { contentType = deeper, parentField = "Parent" },
            fields = new object[] { new { name = "Teacher", type = "reference", referenceType = header }, new { name = "Hours", type = "decimal" } },
        };

        var res = await (await AdminAsync()).PostAsJsonAsync(
            "/api/config/apply", ConfigOfTypes((header, HeaderWithLines(header, line, "decimal", "Hours")), (line, childDoc)), Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(res));
        await AssertStoredLinesAsync(header, line);
    }

    // The lock order. An import of [later, first] must take first before later, as a config apply does.

    private string LockKey(string type)
    {
        using var scope = _fixture.Services.CreateScope();
        return $"barakocms:field-change:{scope.ServiceProvider.GetRequiredService<IDocumentSession>().TenantId}:{type}";
    }

    private async Task<long> AdvisoryLocksAsync(string type, bool granted)
    {
        await using var connection = _fixture.Services.GetRequiredService<IDocumentStore>().Storage.Database.CreateConnection();
        await connection.OpenAsync(Ct);
        await using var count = connection.CreateCommand();
        // A bigint advisory key is stored as its high half in classid and its low half in objid.
        count.CommandText = """
            select count(*) from pg_locks
            where locktype = 'advisory' and objsubid = 1 and granted = @granted
              and classid = ((hashtextextended(@name, 0) >> 32) & 4294967295)::oid
              and objid = (hashtextextended(@name, 0) & 4294967295)::oid
            """;
        count.Parameters.AddWithValue("granted", granted);
        count.Parameters.AddWithValue("name", LockKey(type));
        return (long)(await count.ExecuteScalarAsync(Ct))!;
    }

    private async Task<bool> TryLockFromAnotherSessionAsync(string type)
    {
        await using var connection = _fixture.Services.GetRequiredService<IDocumentStore>().Storage.Database.CreateConnection();
        await connection.OpenAsync(Ct);
        await using var attempt = connection.CreateCommand();
        attempt.CommandText = "select pg_try_advisory_xact_lock(hashtextextended(@name, 0))";
        attempt.Parameters.AddWithValue("name", LockKey(type));
        return (bool)(await attempt.ExecuteScalarAsync(Ct))!;
    }

    private async Task WaitForQueuedLockAsync(string type)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        while (await AdvisoryLocksAsync(type, granted: false) < 1)
        {
            limit.Token.ThrowIfCancellationRequested();
            await Task.Delay(25, limit.Token);
        }
    }

    [Fact]
    public async Task An_apply_takes_the_type_locks_in_name_order_so_it_does_not_hold_a_later_type_while_it_waits_on_an_earlier_one()
    {
        var id = Guid.NewGuid().ToString("n")[..8];
        var (first, later) = ($"lk-a-{id}", $"lk-b-{id}");
        object Entry(string name) => new { name, displayName = name, fields = new object[] { new { name = "Name", type = "string" } } };
        var holder = _fixture.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        Task<HttpResponseMessage> import;
        try
        {
            await barakoCMS.Infrastructure.Jobs.FieldChanges.LockTypeAsync(holder, first, Ct);
            import = (await AdminAsync()).PostAsJsonAsync(
                "/api/config/apply", ConfigOfTypes((later, Entry(later)), (first, Entry(first))), Ct);

            await WaitForQueuedLockAsync(first);

            (await TryLockFromAnotherSessionAsync(later)).Should().BeTrue(
                "the apply waits on the first type before it takes the later one");
        }
        finally
        {
            await holder.DisposeAsync();
        }

        var res = await import;
        res.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(res));
    }

    /// <summary>
    /// Holds the lock of <paramref name="held"/> exclusively, asks for it as <paramref name="spelled"/>
    /// with <paramref name="take"/>, and returns once the ask is queued, then releases and lets it finish.
    /// Both sessions are disposed on every path, so a failed run leaves no transaction open.
    /// </summary>
    private async Task AssertSpellingWaitsOnHeldLockAsync(
        string held, string spelled, Func<IDocumentSession, string, CancellationToken, Task> take)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var holder = store.LightweightSession();
        var waiter = store.LightweightSession();
        Task? wait = null;
        try
        {
            await barakoCMS.Infrastructure.Jobs.FieldChanges.LockTypeAsync(holder, held, Ct);
            wait = take(waiter, spelled, Ct);

            await WaitForQueuedLockAsync(held);
            wait.IsCompleted.Should().BeFalse("the spelling is the held type, so it waits");

            await holder.DisposeAsync();
            await wait;
        }
        finally
        {
            await holder.DisposeAsync();
            if (wait is not null)
            {
                try { await wait; } catch (Exception) { /* the run already failed or was released */ }
            }

            await waiter.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_lock_on_a_type_is_keyed_by_the_normalized_name_so_a_spelling_with_spaces_waits_on_the_stored_name()
    {
        var id = Guid.NewGuid().ToString("n")[..8];

        await AssertSpellingWaitsOnHeldLockAsync($"lk-s-{id}", $" Lk S-{id} ",
            (session, name, ct) => barakoCMS.Infrastructure.Jobs.FieldChanges.LockTypeAsync(session, name, ct));
    }

    [Fact]
    public async Task A_shared_lock_on_a_type_is_keyed_by_the_normalized_name_so_a_spelling_with_spaces_waits_on_the_stored_name()
    {
        var id = Guid.NewGuid().ToString("n")[..8];

        await AssertSpellingWaitsOnHeldLockAsync($"lk-h-{id}", $" Lk H-{id} ",
            (session, name, ct) => barakoCMS.Infrastructure.Jobs.FieldChanges.LockTypeSharedAsync(session, name, ct));
    }

    [Fact]
    public async Task A_config_apply_with_a_source_type_keyed_with_spaces_reads_the_source_as_the_document_has_it()
    {
        var id = Guid.NewGuid().ToString("n")[..8];
        var (source, summary, spelled) = ($"sq-{id}", $"tq-{id}", "Sq " + id);
        await StoreAsync(Type(source, R.Reference("Teacher", summary), R.Plain("Qty", "decimal")));
        var sourceDoc = new
        {
            name = spelled, displayName = spelled,
            fields = new object[] { new { name = "Teacher", type = "reference", referenceType = summary }, new { name = "Qty", type = "string" } },
        };
        var summaryDoc = new
        {
            name = summary, displayName = summary,
            fields = WithName([RollupField("Total", "decimal", source, "Teacher", "sum", "Qty")]),
        };

        var res = await (await AdminAsync()).PostAsJsonAsync(
            "/api/config/apply", ConfigOfTypes((spelled, sourceDoc), (summary, summaryDoc)), Ct);

        await AssertRefusedAsync(res, "rolls up sum of 'Qty', which is a string.");
        await AssertNotStoredAsync(summary);
        (await StoredFieldAsync(source, "Qty"))!.Type.Should().Be("decimal", "a refused apply stores nothing");
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

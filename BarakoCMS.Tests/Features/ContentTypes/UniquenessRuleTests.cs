using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// A content type can declare values only one entry may hold at a time, such as one open time entry
/// per teacher, and every write through the API is refused with 409 when it would leave two entries
/// holding them.
/// </summary>
/// <remarks>
/// A tenant per test, so a test reads only the entries it wrote. One derived host for the class,
/// never disposed, on which a teacher may clock out and reopen their own entry; without that the
/// self transition rule would refuse the moves this class needs.
/// </remarks>
[Collection("Sequential")]
public class UniquenessRuleTests
{
    private static int _ipCounter;

    private static readonly Lock HostGate = new();
    private static WebApplicationFactory<Program>? _host;

    private readonly IntegrationTestFixture _fixture;

    public UniquenessRuleTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string OneOpen = "OneOpenEntryPerTeacher";

    private WebApplicationFactory<Program> Host()
    {
        lock (HostGate)
        {
            return _host ??= _fixture.WithSettings(new Dictionary<string, string?>
            {
                ["Lifecycle:AllowSelfTransition:ClockOut"] = "true",
                ["Lifecycle:AllowSelfTransition:Reopen"] = "true",
            });
        }
    }

    private async Task<string> TenantAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"unq-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    /// <summary>A SuperAdmin of the tenant, standing in for a teacher: each one is its own creator.</summary>
    private async Task<HttpClient> TeacherOfAsync(string tenantSlug)
    {
        var userId = Guid.NewGuid();

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = $"unq-{Guid.NewGuid():n}"[..14],
                Email = $"unq-{Guid.NewGuid():n}@example.com",
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TenantSlug = tenantSlug,
                Status = MembershipStatus.Active,
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            await session.SaveChangesAsync(Ct);
        }

        var client = Host().CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(
                roles: ["SuperAdmin", "Admin"],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = tenantSlug }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenantSlug);
        client.DefaultRequestHeaders.Add(
            TestRemoteIpFilter.Header, $"198.51.104.{Interlocked.Increment(ref _ipCounter) % 200 + 20}");
        return client;
    }

    private static string NewType(string prefix) => $"{prefix}{Guid.NewGuid():n}"[..14];

    private static object Clock() => new
    {
        states = new[] { "Open", "Closed" },
        initialState = "Open",
        transitions = new[]
        {
            new { name = "ClockOut", from = "Open", to = "Closed" },
            new { name = "Reopen", from = "Closed", to = "Open" },
        },
    };

    /// <summary>The time clock: one open entry per teacher.</summary>
    private static async Task<string> TimeClockAsync(HttpClient client, object[]? uniqueness = null)
    {
        var type = NewType("time");
        var response = await client.PostAsJsonAsync("/api/content-types", new
        {
            name = type,
            displayName = "Time entry",
            fields = new[] { new { name = "Note", displayName = "Note", type = "string" } },
            lifecycle = Clock(),
            uniqueness = uniqueness ?? [new { name = OneOpen, fields = new[] { "$createdBy" }, whenState = "Open" }],
        }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return type;
    }

    /// <summary>A badge register: one entry per badge, per amount and per code, whatever its state.</summary>
    private static async Task<string> BadgesAsync(HttpClient client)
    {
        var type = NewType("badge");
        var response = await client.PostAsJsonAsync("/api/content-types", new
        {
            name = type,
            displayName = "Badge",
            fields = new object[]
            {
                new { name = "Badge", displayName = "Badge", type = "string" },
                new { name = "Amount", displayName = "Amount", type = "decimal" },
                new { name = "Code", displayName = "Code", type = "uuid" },
                new { name = "Mail", displayName = "Mail", type = "email" },
                new { name = "Note", displayName = "Note", type = "string" },
            },
            uniqueness = new[]
            {
                new { name = "OneHolderPerBadge", fields = new[] { "Badge" } },
                new { name = "OneHolderPerAmount", fields = new[] { "Amount" } },
                new { name = "OneHolderPerCode", fields = new[] { "Code" } },
                new { name = "OneHolderPerMail", fields = new[] { "Mail" } },
            },
        }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return type;
    }

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string type, Dictionary<string, object> data) =>
        client.PostAsJsonAsync("/api/contents", new { contentType = type, data }, Ct);

    private static Task<HttpResponseMessage> TimeInAsync(HttpClient client, string type) =>
        CreateAsync(client, type, new Dictionary<string, object> { ["Note"] = "in" });

    private static Task<HttpResponseMessage> MoveAsync(HttpClient client, Guid id, string transition) =>
        client.PutAsJsonAsync($"/api/contents/{id}/status", new { id, transition }, Ct);

    private static Task<HttpResponseMessage> UpdateAsync(HttpClient client, Guid id, Dictionary<string, object> data) =>
        client.PutAsJsonAsync($"/api/contents/{id}", new { id, data }, Ct);

    private static async Task<Guid> IdOfAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<List<Content>> EntriesAsync(string tenant, string type)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(tenant);
        var entries = await session.Query<Content>().Where(c => c.ContentType == type).ToListAsync(Ct);
        return entries.ToList();
    }

    private async Task<Content> EntryAsync(string tenant, Guid id)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(tenant);
        var entry = await session.LoadAsync<Content>(id, Ct);
        entry.Should().NotBeNull();
        return entry!;
    }

    private static async Task<string> BodyAsync(HttpResponseMessage response) =>
        (await response.Content.ReadAsStringAsync(Ct)).Replace("\\u0027", "'");

    // ---- the time clock ---------------------------------------------------------------------------

    [Fact]
    public async Task A_second_time_in_is_refused_until_the_first_entry_is_clocked_out()
    {
        var tenant = await TenantAsync();
        var teacher = await TeacherOfAsync(tenant);
        var type = await TimeClockAsync(teacher);

        var first = await IdOfAsync(await TimeInAsync(teacher, type));

        var second = await TimeInAsync(teacher, type);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict, await BodyAsync(second));

        var clockedOut = await MoveAsync(teacher, first, "ClockOut");
        clockedOut.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(clockedOut));

        var again = await TimeInAsync(teacher, type);
        again.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(again));

        var entries = await EntriesAsync(tenant, type);
        entries.Should().HaveCount(2, "the refused time in stored nothing");
        entries.Count(e => (e.LifecycleState ?? "Open") == "Open").Should().Be(1);
    }

    [Fact]
    public async Task The_refusal_names_the_rule_and_not_the_entry_holding_the_value()
    {
        var tenant = await TenantAsync();
        var teacher = await TeacherOfAsync(tenant);
        var type = await TimeClockAsync(teacher);
        var first = await IdOfAsync(await TimeInAsync(teacher, type));

        var second = await TimeInAsync(teacher, type);
        var body = await BodyAsync(second);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
        body.Should().Contain(OneOpen).And.Contain("while Open");
        body.Should().NotContain(first.ToString(), "the caller may have no right to read the entry in the way");
    }

    [Fact]
    public async Task Another_teacher_can_time_in_while_the_first_has_an_open_entry()
    {
        var tenant = await TenantAsync();
        var first = await TeacherOfAsync(tenant);
        var second = await TeacherOfAsync(tenant);
        var type = await TimeClockAsync(first);

        (await TimeInAsync(first, type)).StatusCode.Should().Be(HttpStatusCode.OK);

        var other = await TimeInAsync(second, type);
        other.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(other));
        (await EntriesAsync(tenant, type)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Time_ins_sent_at_once_by_one_teacher_leave_exactly_one_entry()
    {
        var tenant = await TenantAsync();
        var teacher = await TeacherOfAsync(tenant);
        var type = await TimeClockAsync(teacher);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => TimeInAsync(teacher, type)));

        responses.Should().HaveCount(8);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(7);
        (await EntriesAsync(tenant, type)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Reopening_a_closed_entry_while_the_teacher_has_an_open_one_is_refused()
    {
        var tenant = await TenantAsync();
        var teacher = await TeacherOfAsync(tenant);
        var type = await TimeClockAsync(teacher);

        var monday = await IdOfAsync(await TimeInAsync(teacher, type));
        (await MoveAsync(teacher, monday, "ClockOut")).StatusCode.Should().Be(HttpStatusCode.OK);
        await IdOfAsync(await TimeInAsync(teacher, type));

        var reopen = await MoveAsync(teacher, monday, "Reopen");

        reopen.StatusCode.Should().Be(HttpStatusCode.Conflict, await BodyAsync(reopen));
        (await BodyAsync(reopen)).Should().Contain(OneOpen);
        (await EntryAsync(tenant, monday)).LifecycleState.Should().Be("Closed");
    }

    [Fact]
    public async Task A_type_with_no_rule_takes_two_open_entries_from_one_teacher()
    {
        var tenant = await TenantAsync();
        var teacher = await TeacherOfAsync(tenant);
        var type = NewType("free");
        (await teacher.PostAsJsonAsync("/api/content-types", new
        {
            name = type,
            displayName = "Time entry",
            fields = new[] { new { name = "Note", displayName = "Note", type = "string" } },
            lifecycle = Clock(),
        }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await TimeInAsync(teacher, type)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await TimeInAsync(teacher, type)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await EntriesAsync(tenant, type)).Should().HaveCount(2);
    }

    // ---- how values compare -----------------------------------------------------------------------

    [Fact]
    public async Task An_update_to_a_value_another_entry_holds_is_refused_and_the_entry_keeps_its_own()
    {
        var tenant = await TenantAsync();
        var client = await TeacherOfAsync(tenant);
        var type = await BadgesAsync(client);

        await IdOfAsync(await CreateAsync(client, type, new() { ["Badge"] = "B-1" }));
        var second = await IdOfAsync(await CreateAsync(client, type, new() { ["Badge"] = "B-2" }));

        var update = await UpdateAsync(client, second, new() { ["Badge"] = "B-1" });

        update.StatusCode.Should().Be(HttpStatusCode.Conflict, await BodyAsync(update));
        (await BodyAsync(update)).Should().Contain("OneHolderPerBadge");
        (await EntryAsync(tenant, second)).Data["Badge"].ToString().Should().Be("B-2");
    }

    [Fact]
    public async Task Text_is_compared_exactly_so_case_and_spaces_make_another_value()
    {
        var tenant = await TenantAsync();
        var client = await TeacherOfAsync(tenant);
        var type = await BadgesAsync(client);

        (await CreateAsync(client, type, new() { ["Badge"] = "B-1" })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await CreateAsync(client, type, new() { ["Badge"] = "B-1" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CreateAsync(client, type, new() { ["Badge"] = "b-1" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CreateAsync(client, type, new() { ["Badge"] = "B-1 " })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await EntriesAsync(tenant, type)).Should().HaveCount(3);
    }

    [Fact]
    public async Task Numbers_are_compared_by_value()
    {
        var tenant = await TenantAsync();
        var client = await TeacherOfAsync(tenant);
        var type = await BadgesAsync(client);

        (await CreateAsync(client, type, new() { ["Amount"] = 1.5m })).StatusCode.Should().Be(HttpStatusCode.OK);

        var same = await CreateAsync(client, type, new() { ["Amount"] = 1.50m });
        same.StatusCode.Should().Be(HttpStatusCode.Conflict, await BodyAsync(same));
        (await BodyAsync(same)).Should().Contain("OneHolderPerAmount");

        (await CreateAsync(client, type, new() { ["Amount"] = 1.25m })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_id_is_compared_without_regard_to_case_braces_or_dashes()
    {
        var tenant = await TenantAsync();
        var client = await TeacherOfAsync(tenant);
        var type = await BadgesAsync(client);
        var code = Guid.NewGuid();

        (await CreateAsync(client, type, new() { ["Code"] = code.ToString("D") })).StatusCode.Should().Be(HttpStatusCode.OK);

        var respelt = await CreateAsync(client, type, new() { ["Code"] = code.ToString("B").ToUpperInvariant() });
        respelt.StatusCode.Should().Be(HttpStatusCode.Conflict, await BodyAsync(respelt));

        var plain = await CreateAsync(client, type, new() { ["Code"] = code.ToString("N") });
        plain.StatusCode.Should().Be(HttpStatusCode.Conflict, await BodyAsync(plain));
    }

    [Fact]
    public async Task An_email_is_compared_with_capitals_from_A_to_Z_lowered()
    {
        var tenant = await TenantAsync();
        var client = await TeacherOfAsync(tenant);
        var type = await BadgesAsync(client);

        (await CreateAsync(client, type, new() { ["Mail"] = "teacher@school.example" })).StatusCode.Should().Be(HttpStatusCode.OK);

        var shouted = await CreateAsync(client, type, new() { ["Mail"] = "Teacher@School.Example" });
        shouted.StatusCode.Should().Be(HttpStatusCode.Conflict, await BodyAsync(shouted));
        (await BodyAsync(shouted)).Should().Contain("OneHolderPerMail");

        (await CreateAsync(client, type, new() { ["Mail"] = "other@school.example" })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_entry_with_nothing_in_a_compared_field_is_outside_the_rule()
    {
        var tenant = await TenantAsync();
        var client = await TeacherOfAsync(tenant);
        var type = await BadgesAsync(client);

        (await CreateAsync(client, type, new() { ["Note"] = "no badge" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CreateAsync(client, type, new() { ["Note"] = "no badge either" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CreateAsync(client, type, new() { ["Badge"] = "" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CreateAsync(client, type, new() { ["Badge"] = "" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await EntriesAsync(tenant, type)).Should().HaveCount(4);
    }

    // ---- declaring a rule -------------------------------------------------------------------------

    [Fact]
    public async Task The_rules_are_returned_with_the_type()
    {
        var tenant = await TenantAsync();
        var client = await TeacherOfAsync(tenant);
        var type = await TimeClockAsync(client);

        JsonElement? found = null;
        for (var page = 1; page <= 10 && found is null; page++)
        {
            using var doc = JsonDocument.Parse(
                await client.GetStringAsync($"/api/content-types?page={page}&pageSize=100", Ct));
            var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
            found = items
                .Where(i => i.GetProperty("name").GetString() == type)
                .Select(i => (JsonElement?)i.Clone())
                .FirstOrDefault();
            if (items.Count < 100) break;
        }

        found.Should().NotBeNull();
        var rules = found!.Value.GetProperty("uniqueness").EnumerateArray().ToList();
        rules.Should().HaveCount(1);
        rules[0].GetProperty("name").GetString().Should().Be(OneOpen);
        rules[0].GetProperty("fields").EnumerateArray().Select(f => f.GetString()).Should().Equal("$createdBy");
        rules[0].GetProperty("whenState").GetString().Should().Be("Open");
    }

    [Theory]
    [InlineData("Nope", null, "which the type does not declare")]
    [InlineData("Secret", null, "which is Sensitive")]
    [InlineData("Tags", null, "a rule compares a field holding one text, number or boolean")]
    [InlineData("Note", "Lunch", "which is not a declared state")]
    [InlineData("When", null, "a rule compares a field holding one text, number or boolean")]
    [InlineData("Day", null, "a rule compares a field holding one text, number or boolean")]
    [InlineData("At", null, "a rule compares a field holding one text, number or boolean")]
    public async Task A_rule_the_type_cannot_hold_is_refused_when_the_type_is_created(
        string field, string? whenState, string expected)
    {
        var tenant = await TenantAsync();
        var client = await TeacherOfAsync(tenant);

        var response = await client.PostAsJsonAsync("/api/content-types", new
        {
            name = NewType("bad"),
            displayName = "Bad",
            fields = new object[]
            {
                new { name = "Note", displayName = "Note", type = "string" },
                new { name = "Secret", displayName = "Secret", type = "string", sensitivity = "Sensitive" },
                new { name = "Tags", displayName = "Tags", type = "array" },
                new { name = "When", displayName = "When", type = "datetime" },
                new { name = "Day", displayName = "Day", type = "date" },
                new { name = "At", displayName = "At", type = "time" },
            },
            lifecycle = Clock(),
            uniqueness = new[] { new { name = "Probe", fields = new[] { field }, whenState } },
        }, Ct);

        var body = await BodyAsync(response);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain(expected);
    }

    [Fact]
    public async Task A_rule_with_a_state_on_a_type_with_no_lifecycle_is_refused()
    {
        var tenant = await TenantAsync();
        var client = await TeacherOfAsync(tenant);

        var response = await client.PostAsJsonAsync("/api/content-types", new
        {
            name = NewType("bad"),
            displayName = "Bad",
            fields = new[] { new { name = "Note", displayName = "Note", type = "string" } },
            uniqueness = new[] { new { name = "Probe", fields = new[] { "Note" }, whenState = "Open" } },
        }, Ct);

        var body = await BodyAsync(response);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("the type declares no lifecycle");
    }
}

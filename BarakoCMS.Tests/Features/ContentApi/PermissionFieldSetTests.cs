using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.ContentApi;

/// <summary>
/// Readable and writable field sets on a permission rule, through the API: a teacher updates
/// attendance and not grades, and a student reads their own notes and nobody else's.
/// </summary>
/// <remarks>
/// Each refusal carries its control: the same caller, the same entry, a field the set does allow.
/// Rows are found by id, never by position, since the database is shared with every other class.
/// </remarks>
[Collection("Sequential")]
public class PermissionFieldSetTests
{
    private static int _sequence;

    private readonly IntegrationTestFixture _factory;

    public PermissionFieldSetTests(IntegrationTestFixture factory) => _factory = factory;

    private static readonly Dictionary<string, object> OwnRecord = new()
    {
        ["$createdBy"] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_USER" },
    };

    /// <summary>A record type: a name, attendance, a grade and notes.</summary>
    private async Task<string> TypeAsync()
    {
        var type = $"fieldset{Guid.NewGuid().ToString("n")[..10]}";

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = type, DisplayName = type,
            Fields =
            [
                new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string" },
                new FieldDefinition { Name = "Attendance", DisplayName = "Attendance", Type = "string" },
                new FieldDefinition { Name = "Grade", DisplayName = "Grade", Type = "string" },
                new FieldDefinition { Name = "Notes", DisplayName = "Notes", Type = "string" },
                new FieldDefinition { Name = "Score", DisplayName = "Score", Type = "decimal" },
                new FieldDefinition { Name = "Count", DisplayName = "Count", Type = "int" },
                new FieldDefinition { Name = "Meta", DisplayName = "Meta", Type = "json" },
                new FieldDefinition
                {
                    Name = "Salary", DisplayName = "Salary", Type = "decimal", Sensitivity = SensitivityLevel.Sensitive,
                },
            ],
        });

        await session.SaveChangesAsync();
        return type;
    }

    /// <summary>A user holding one role per permission list given.</summary>
    private Task<(HttpClient Client, Guid UserId)> CallerAsync(params List<ContentTypePermission>[] roles) =>
        CallerHoldingAsync([], roles);

    /// <summary>The same, the first role also holding these system capabilities.</summary>
    private async Task<(HttpClient Client, Guid UserId)> CallerHoldingAsync(
        string[] capabilities, params List<ContentTypePermission>[] roles)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var held = roles.Select((permissions, i) => new Role
        {
            Id = Guid.NewGuid(),
            Name = $"FieldSet_{Guid.NewGuid():n}",
            Permissions = permissions,
            SystemCapabilities = i == 0 ? [.. capabilities] : [],
        }).ToList();

        foreach (var role in held)
            session.Store(role);

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"fieldset_{Guid.NewGuid():n}",
            Email = $"fieldset_{Guid.NewGuid():n}@example.com",
            RoleIds = held.Select(r => r.Id).ToList(),
        };
        session.Store(user);
        await session.SaveChangesAsync();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: held.Select(r => r.Name).ToArray(), userId: user.Id.ToString()));

        return (client, user.Id);
    }

    private async Task<HttpClient> SuperAdminAsync()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("SuperAdmin"));
        return client;
    }

    /// <summary>Stores an entry directly, so a test can say who created it.</summary>
    private async Task<Guid> StoreAsync(string type, Guid createdBy, Dictionary<string, object> data)
    {
        using var scope = _factory.Services.CreateScope();
        await using var session = scope.ServiceProvider.GetRequiredService<IDocumentStore>().LightweightSession();

        var created = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Interlocked.Increment(ref _sequence));
        var content = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            Status = ContentStatus.Published,
            CreatedBy = createdBy,
            CreatedAt = created,
            UpdatedAt = created,
            Data = data,
        };

        session.Store(content);
        await session.SaveChangesAsync();
        return content.Id;
    }

    /// <summary>Creates an entry through the API, so it has the stream an update appends to.</summary>
    private static async Task<Guid> CreateAsync(
        HttpClient admin, string type, Dictionary<string, object> data, SensitivityLevel sensitivity = SensitivityLevel.Public)
    {
        var res = await admin.PostAsJsonAsync("/api/contents", new { contentType = type, data, sensitivity });
        res.IsSuccessStatusCode.Should().BeTrue("creating an entry returned {0}: {1}",
            res.StatusCode, await res.Content.ReadAsStringAsync());

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static Dictionary<string, object> Record(string name) => new()
    {
        ["Name"] = name,
        ["Attendance"] = "present",
        ["Grade"] = "B",
        ["Notes"] = $"notes about {name}",
    };

    private static async Task<JsonElement> DataAsync(HttpClient client, Guid id)
    {
        var res = await client.GetAsync($"/api/contents/{id}");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid id, Dictionary<string, object> data) =>
        client.PutAsJsonAsync($"/api/contents/{id}", new { id, data });

    /// <summary>The data of every listed row with one of these ids, read page by page.</summary>
    private static async Task<Dictionary<Guid, JsonElement>> ListedAsync(HttpClient client, string query, params Guid[] ids)
    {
        var found = new Dictionary<Guid, JsonElement>();

        for (var page = 1; page <= 40; page++)
        {
            var res = await client.GetAsync($"/api/contents?page={page}&pageSize=100&{query}");
            res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();

            foreach (var item in items)
            {
                var id = item.GetProperty("id").GetGuid();
                if (ids.Contains(id))
                    found[id] = item.GetProperty("data").Clone();
            }

            if (items.Count < 100) break;
        }

        return found;
    }

    private static List<ContentTypePermission> Teacher(string type) =>
    [
        new()
        {
            ContentTypeSlug = type,
            Read = new PermissionRule { Enabled = true },
            Update = new PermissionRule { Enabled = true, WritableFields = ["Attendance"] },
        },
    ];

    [Fact]
    public async Task A_writable_set_without_grade_refuses_a_change_to_grade_and_allows_one_to_attendance()
    {
        var type = await TypeAsync();
        var (teacher, _) = await CallerAsync(Teacher(type));
        var admin = await SuperAdminAsync();
        var id = await CreateAsync(admin, type, Record("ana"));

        var grade = Record("ana");
        grade["Grade"] = "A";
        var refused = await PutAsync(teacher, id, grade);
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, await refused.Content.ReadAsStringAsync());
        (await refused.Content.ReadAsStringAsync()).Should().Contain("Grade");
        (await DataAsync(admin, id)).GetProperty("Grade").GetString().Should().Be("B");

        var attendance = Record("ana");
        attendance["Attendance"] = "absent";
        var allowed = await PutAsync(teacher, id, attendance);
        allowed.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", allowed.StatusCode, await allowed.Content.ReadAsStringAsync());

        var stored = await DataAsync(admin, id);
        stored.GetProperty("Attendance").GetString().Should().Be("absent");
        stored.GetProperty("Grade").GetString().Should().Be("B", "sent unchanged, it is not a write");
    }

    [Fact]
    public async Task An_update_that_leaves_out_a_field_outside_the_writable_set_keeps_its_stored_value()
    {
        var type = await TypeAsync();
        var (teacher, _) = await CallerAsync(Teacher(type));
        var admin = await SuperAdminAsync();
        var id = await CreateAsync(admin, type, Record("ben"));

        var res = await PutAsync(teacher, id, new Dictionary<string, object>
        {
            ["Name"] = "ben", ["Attendance"] = "late", ["Notes"] = "notes about ben",
        });
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());

        var stored = await DataAsync(admin, id);
        stored.GetProperty("Attendance").GetString().Should().Be("late");
        stored.GetProperty("Grade").GetString().Should().Be("B", "leaving a field out is not a way to delete it");
    }

    [Fact]
    public async Task A_create_giving_a_value_to_a_field_outside_the_create_set_is_refused()
    {
        var type = await TypeAsync();
        List<ContentTypePermission> permissions =
        [
            new()
            {
                ContentTypeSlug = type,
                Create = new PermissionRule { Enabled = true, WritableFields = ["Name", "Attendance"] },
            },
        ];
        var (clerk, _) = await CallerAsync(permissions);

        var refused = await clerk.PostAsJsonAsync("/api/contents", new { contentType = type, data = Record("cy") });
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, await refused.Content.ReadAsStringAsync());

        var allowed = await clerk.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["Name"] = "cy", ["Attendance"] = "present" },
        });
        allowed.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", allowed.StatusCode, await allowed.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Two roles on one user: everyone's names, and their own notes. The own-record rule's set is
    /// what shows Notes, so it shows them on the entry that rule grants and nowhere else.
    /// </summary>
    private static List<ContentTypePermission>[] Student(string type) =>
    [
        [new() { ContentTypeSlug = type, Read = new PermissionRule { Enabled = true, ReadableFields = ["Name"] } }],
        [new()
        {
            ContentTypeSlug = type,
            Read = new PermissionRule { Enabled = true, Conditions = OwnRecord, ReadableFields = ["Name", "Notes"] },
        }],
    ];

    [Fact]
    public async Task A_readable_set_on_an_own_record_rule_shows_its_fields_to_the_owner_only()
    {
        var type = await TypeAsync();
        var (student, userId) = await CallerAsync(Student(type));
        var eve = $"eve{Guid.NewGuid():n}";
        var mine = await StoreAsync(type, userId, Record("dee"));
        var theirs = await StoreAsync(type, Guid.NewGuid(), Record(eve));

        var own = await DataAsync(student, mine);
        own.GetProperty("Notes").GetString().Should().Be("notes about dee");
        own.TryGetProperty("Grade", out _).Should().BeFalse("no rule shows Grade");

        var other = await DataAsync(student, theirs);
        other.GetProperty("Name").GetString().Should().Be(eve);
        other.TryGetProperty("Notes", out _).Should().BeFalse("only the own-record rule shows Notes");

        var listed = await ListedAsync(student, $"contentType={type}", mine, theirs);
        listed.Should().HaveCount(2);
        listed[mine].GetProperty("Notes").GetString().Should().Be("notes about dee");
        listed[theirs].TryGetProperty("Notes", out _).Should().BeFalse();

        var across = await ListedAsync(student, $"search={eve}", theirs);
        across.Should().HaveCount(1, "the name is shown, so a search on it finds the row");
        across[theirs].TryGetProperty("Notes", out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_field_filter_on_a_field_outside_the_readable_set_is_refused()
    {
        var type = await TypeAsync();
        var (student, _) = await CallerAsync(Student(type));
        var theirs = await StoreAsync(type, Guid.NewGuid(), Record("fay"));

        var refused = await student.GetAsync($"/api/contents?contentType={type}&filter[Notes][eq]=notes%20about%20fay");
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "Notes is shown on the caller's own entries only, so matching it would read it on everyone's");

        var allowed = await ListedAsync(student, $"contentType={type}&filter[Name][eq]=fay", theirs);
        allowed.Should().HaveCount(1, "Name is shown on every entry, which is the control");
    }

    [Fact]
    public async Task A_search_does_not_match_a_value_in_a_field_the_caller_is_not_shown()
    {
        var type = await TypeAsync();
        var (student, _) = await CallerAsync(Student(type));
        var admin = await SuperAdminAsync();
        var marker = $"secret{Guid.NewGuid():n}";

        var record = Record("gus");
        record["Notes"] = marker;
        var theirs = await StoreAsync(type, Guid.NewGuid(), record);

        (await ListedAsync(admin, $"contentType={type}&search={marker}", theirs)).Should().HaveCount(1,
            "the term is in the entry, which is the control");

        (await ListedAsync(student, $"contentType={type}&search={marker}", theirs)).Should().BeEmpty(
            "a named type's search runs only over the fields the caller is shown");
        (await ListedAsync(student, $"search={marker}", theirs)).Should().BeEmpty(
            "a search across types keeps a row only when a value the caller is shown holds the term");
    }

    /// <summary>The value of a field in every version of an entry's history that carries data.</summary>
    private static async Task<List<JsonElement>> HistoryDataAsync(HttpClient client, Guid id)
    {
        var res = await client.GetAsync($"/api/contents/{id}/history");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .Where(v => v.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            .Select(v => v.GetProperty("data").Clone())
            .ToList();
    }

    [Fact]
    public async Task History_shows_the_fields_the_rules_granting_that_entry_show()
    {
        var type = await TypeAsync();
        List<ContentTypePermission> creates = [new() { ContentTypeSlug = type, Create = new PermissionRule { Enabled = true } }];
        var (student, _) = await CallerAsync(Student(type).Append(creates).ToArray());
        var admin = await SuperAdminAsync();

        var mine = await CreateAsync(student, type, Record("hal"));
        var theirs = await CreateAsync(admin, type, Record("ida"));

        var own = await HistoryDataAsync(student, mine);
        own.Should().NotBeEmpty();
        own.Should().AllSatisfy(data =>
        {
            data.GetProperty("Notes").GetString().Should().Be("notes about hal", "the own-record rule grants this entry");
            data.TryGetProperty("Grade", out _).Should().BeFalse("no rule shows Grade");
        });

        var other = await HistoryDataAsync(student, theirs);
        other.Should().NotBeEmpty();
        other.Should().AllSatisfy(data =>
        {
            data.GetProperty("Name").GetString().Should().Be("ida");
            data.TryGetProperty("Notes", out _).Should().BeFalse("only the own-record rule shows Notes");
        });
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Forbidden)]
    [InlineData(true, HttpStatusCode.OK)]
    public async Task A_condition_following_a_reference_compares_only_a_field_the_caller_is_shown(
        bool instructorShown, HttpStatusCode expected)
    {
        var suffix = Guid.NewGuid().ToString("n")[..10];
        var classes = $"setclass{suffix}";
        var enrollments = $"setenrol{suffix}";

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = classes, DisplayName = classes,
                Fields =
                [
                    new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                    new FieldDefinition { Name = "InstructorUser", DisplayName = "Instructor", Type = "string" },
                ],
            });
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = enrollments, DisplayName = enrollments,
                Fields =
                [
                    new FieldDefinition { Name = "Student", DisplayName = "Student", Type = "string" },
                    new FieldDefinition { Name = "Class", DisplayName = "Class", Type = "reference", ReferenceType = classes },
                ],
            });
            await session.SaveChangesAsync();
        }

        List<string> shown = instructorShown ? ["Title", "InstructorUser"] : ["Title"];
        List<ContentTypePermission> permissions =
        [
            new() { ContentTypeSlug = classes, Read = new PermissionRule { Enabled = true, ReadableFields = shown } },
            new()
            {
                ContentTypeSlug = enrollments,
                Read = new PermissionRule
                {
                    Enabled = true,
                    Conditions = new Dictionary<string, object>
                    {
                        ["Class.InstructorUser"] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_USER" },
                    },
                },
            },
        ];
        var (instructor, userId) = await CallerAsync(permissions);

        var taught = await StoreAsync(classes, Guid.NewGuid(), new() { ["Title"] = "algebra", ["InstructorUser"] = userId.ToString() });
        var enrollment = await StoreAsync(enrollments, Guid.NewGuid(), new() { ["Student"] = "jo", ["Class"] = taught.ToString() });

        (await instructor.GetAsync($"/api/contents/{enrollment}")).StatusCode.Should().Be(expected,
            "whether an enrollment matches says what the class's InstructorUser holds, so the caller has to be shown it");
    }

    [Fact]
    public async Task A_type_with_no_field_sets_reads_and_writes_every_field_as_before()
    {
        var type = await TypeAsync();
        List<ContentTypePermission> permissions =
        [
            new()
            {
                ContentTypeSlug = type,
                Read = new PermissionRule { Enabled = true },
                Update = new PermissionRule { Enabled = true },
            },
        ];
        var (editor, _) = await CallerAsync(permissions);
        var admin = await SuperAdminAsync();
        var id = await CreateAsync(admin, type, Record("ivy"));

        var read = await DataAsync(editor, id);
        read.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(new[] { "Name", "Attendance", "Grade", "Notes" });

        var changed = Record("ivy");
        changed["Grade"] = "A";
        changed.Remove("Notes");
        var res = await PutAsync(editor, id, changed);
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());

        var stored = await DataAsync(admin, id);
        stored.GetProperty("Grade").GetString().Should().Be("A");
        stored.TryGetProperty("Notes", out _).Should().BeFalse("with no set, an update replaces the data as it always has");
    }

    /// <summary>Why the caller may not read the entry an update names.</summary>
    public enum Unread
    {
        NoReadRule,
        NoReadRuleGrantsIt,
        SensitiveEntry,
    }

    [Theory]
    [InlineData(Unread.NoReadRule)]
    [InlineData(Unread.NoReadRuleGrantsIt)]
    [InlineData(Unread.SensitiveEntry)]
    public async Task On_an_entry_the_caller_may_not_read_a_wrong_and_a_right_guess_get_the_same_answer(Unread why)
    {
        var type = await TypeAsync();
        var admin = await SuperAdminAsync();

        var read = why switch
        {
            Unread.NoReadRule => new PermissionRule(),
            Unread.NoReadRuleGrantsIt => new PermissionRule { Enabled = true, Conditions = OwnRecord, ReadableFields = ["Name", "Grade"] },
            _ => new PermissionRule { Enabled = true },
        };
        List<ContentTypePermission> permissions =
        [
            new()
            {
                ContentTypeSlug = type,
                Read = read,
                Update = new PermissionRule { Enabled = true, WritableFields = ["Attendance"] },
            },
        ];
        var (caller, _) = await CallerAsync(permissions);

        var sensitivity = why == Unread.SensitiveEntry ? SensitivityLevel.Sensitive : SensitivityLevel.Public;
        var id = await CreateAsync(admin, type, Record("kim"), sensitivity);

        // The stored grade is B. A is the wrong guess, B the right one.
        var answers = new List<HttpStatusCode>();
        foreach (var guess in new[] { "A", "B" })
        {
            var data = Record("kim");
            data["Grade"] = guess;
            data["Attendance"] = $"seen {guess}";
            answers.Add((await PutAsync(caller, id, data)).StatusCode);
        }

        answers.Should().HaveCount(2);
        answers.Should().OnlyContain(status => status == HttpStatusCode.OK,
            "an answer that differed by guess would tell the caller a value they may not read");

        var stored = await DataAsync(admin, id);
        stored.GetProperty("Grade").GetString().Should().Be("B", "a field outside the writable set is put back");
        stored.GetProperty("Attendance").GetString().Should().Be("seen B", "the field the writable set names is written");
    }

    [Fact]
    public async Task A_value_sent_back_in_another_spelling_of_the_same_json_is_not_a_change()
    {
        var type = await TypeAsync();
        var (teacher, _) = await CallerAsync(Teacher(type));
        var admin = await SuperAdminAsync();

        var record = Record("lou");
        record["Score"] = 1.5m;
        record["Count"] = 1;
        record["Meta"] = new Dictionary<string, object> { ["a"] = 1, ["b"] = new Dictionary<string, object> { ["c"] = 2 } };
        var id = await CreateAsync(admin, type, record);

        var sent = Record("lou");
        sent["Attendance"] = "absent";
        sent["Score"] = 1.50m;
        sent["Count"] = 1.0m;
        sent["Meta"] = new Dictionary<string, object> { ["b"] = new Dictionary<string, object> { ["c"] = 2 }, ["a"] = 1 };

        var res = await PutAsync(teacher, id, sent);
        res.IsSuccessStatusCode.Should().BeTrue("the same values, written differently, got {0}: {1}",
            res.StatusCode, await res.Content.ReadAsStringAsync());

        var stored = await DataAsync(admin, id);
        stored.GetProperty("Attendance").GetString().Should().Be("absent");
        stored.GetProperty("Meta").GetProperty("b").GetProperty("c").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task The_rollback_response_applies_the_read_rules()
    {
        var type = await TypeAsync();
        var admin = await SuperAdminAsync();

        List<ContentTypePermission> permissions =
        [
            new()
            {
                ContentTypeSlug = type,
                Read = new PermissionRule { Enabled = true, ReadableFields = ["Name", "Attendance", "Salary"] },
                Update = new PermissionRule { Enabled = true },
            },
        ];
        var (restorer, _) = await CallerHoldingAsync([SystemCapabilities.RollbackContent], permissions);

        var record = Record("max");
        record["Salary"] = 5000m;
        var id = await CreateAsync(admin, type, record);

        var versions = await admin.GetAsync($"/api/contents/{id}/history");
        versions.StatusCode.Should().Be(HttpStatusCode.OK);
        using var listed = JsonDocument.Parse(await versions.Content.ReadAsStringAsync());
        listed.RootElement.GetProperty("items").GetArrayLength().Should().BeGreaterThan(0);
        var versionId = listed.RootElement.GetProperty("items")[0].GetProperty("versionId").GetGuid();

        var res = await restorer.PostAsync($"/api/contents/{id}/rollback/{versionId}", null);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("Name").GetString().Should().Be("max", "a field the caller is shown is in the response");
        data.TryGetProperty("Notes", out _).Should().BeFalse("the Read rule does not show Notes");
        data.TryGetProperty("Grade", out _).Should().BeFalse("the Read rule does not show Grade");
        data.GetProperty("Salary").GetRawText().Should().NotBe("5000",
            "Salary is Sensitive and the caller does not hold view_sensitive, so it is masked as a GET masks it");
    }

    [Fact]
    public async Task A_transition_carrying_a_field_the_rules_granting_that_entry_show_writes_it()
    {
        var suffix = Guid.NewGuid().ToString("n")[..10];
        var type = $"settrans{suffix}";

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = type, DisplayName = type,
                Fields =
                [
                    new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string" },
                    new FieldDefinition { Name = "Notes", DisplayName = "Notes", Type = "string" },
                ],
                Lifecycle = new LifecycleDefinition
                {
                    States = ["Open", "Closed"],
                    InitialState = "Open",
                    Transitions = [new StateTransition { Name = "Close", From = "Open", To = "Closed", OptionalFields = ["Notes"] }],
                },
            });
            await session.SaveChangesAsync();
        }

        // Everyone's names, and the caller's own record, by its Name, with notes. Ownership by a
        // field rather than by creator, since a creator may not move their own entry on.
        var everyone = new ContentTypePermission
        {
            ContentTypeSlug = type,
            Read = new PermissionRule { Enabled = true, ReadableFields = ["Name"] },
            Transitions = new(StringComparer.OrdinalIgnoreCase) { ["Close"] = new PermissionRule { Enabled = true } },
        };
        var own = new ContentTypePermission
        {
            ContentTypeSlug = type,
            Read = new PermissionRule
            {
                Enabled = true,
                Conditions = new Dictionary<string, object>
                {
                    ["Name"] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_USER" },
                },
                ReadableFields = ["Name", "Notes"],
            },
        };
        var (student, userId) = await CallerAsync([everyone], [own]);
        var admin = await SuperAdminAsync();

        var mine = await CreateAsync(admin, type, new() { ["Name"] = userId.ToString(), ["Notes"] = "old" });
        var theirs = await CreateAsync(admin, type, new() { ["Name"] = "someone else", ["Notes"] = "old" });

        foreach (var id in new[] { mine, theirs })
        {
            var res = await student.PutAsJsonAsync($"/api/contents/{id}/status", new
            {
                id,
                transition = "Close",
                data = new Dictionary<string, object> { ["Notes"] = "new" },
            });
            res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        }

        (await DataAsync(admin, mine)).GetProperty("Notes").GetString().Should().Be("new",
            "the own-record rule shows Notes on this entry, so the caller may send it");
        (await DataAsync(admin, theirs)).GetProperty("Notes").GetString().Should().Be("old",
            "on another entry Notes is not shown, so it is put back");
    }
}

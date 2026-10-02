using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.ContentApi;

/// <summary>
/// A rule that follows a reference, through the API: an instructor reads the enrollments of the
/// classes they teach, where the enrollment names a class and only the class names the instructor.
/// </summary>
/// <remarks>
/// The rule is <c>Class.InstructorUser eq $CURRENT_USER</c>. Every way the reference can fail to
/// resolve has to deny, and each of those tests carries its own control: the same caller reads a
/// sibling enrollment whose reference does resolve, so a refusal is the case under test and not a
/// rule that grants nothing.
/// </remarks>
[Collection("Sequential")]
public class ReferenceConditionAccessTests
{
    private const string Follows = "Class.InstructorUser";

    private static int _sequence;

    private readonly IntegrationTestFixture _factory;

    public ReferenceConditionAccessTests(IntegrationTestFixture factory) => _factory = factory;

    private static PermissionRule Where(string key, string op, object expected) => new()
    {
        Enabled = true,
        Conditions = new Dictionary<string, object> { [key] = new Dictionary<string, object> { [op] = expected } },
    };

    private static PermissionRule Teaches() => Where(Follows, "_eq", "$CURRENT_USER");

    private static PermissionRule OwnClass() => Where("InstructorUser", "_eq", "$CURRENT_USER");

    private static ContentTypePermission Classes(string type) => new() { ContentTypeSlug = type, Read = OwnClass() };

    private static ContentTypePermission Enrollments(string type) => new()
    {
        ContentTypeSlug = type,
        Read = Teaches(),
        Update = Teaches(),
    };

    /// <summary>A class type naming its instructor, and an enrollment type pointing at a class.</summary>
    private async Task<(string Classes, string Enrollments)> TypesAsync(LifecycleDefinition? lifecycle = null)
    {
        var suffix = Guid.NewGuid().ToString("n")[..10];
        var classes = $"refclass{suffix}";
        var enrollments = $"refenrol{suffix}";

        using var scope = _factory.Services.CreateScope();
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
            Id = Guid.NewGuid(), Name = enrollments, DisplayName = enrollments, Lifecycle = lifecycle,
            Fields =
            [
                new FieldDefinition { Name = "Student", DisplayName = "Student", Type = "string" },
                new FieldDefinition { Name = "Class", DisplayName = "Class", Type = "reference", ReferenceType = classes },
            ],
        });

        await session.SaveChangesAsync();
        return (classes, enrollments);
    }

    private async Task ChangeTypeAsync(string name, Action<ContentTypeDefinition> change)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var definition = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == name);
        definition.Should().NotBeNull();

        change(definition!);
        session.Store(definition!);
        await session.SaveChangesAsync();
    }

    /// <summary>A user whose one role holds exactly these permissions.</summary>
    private Task<(HttpClient Client, Guid UserId)> CallerAsync(params ContentTypePermission[] permissions) =>
        CallerHoldingAsync([], permissions);

    /// <summary>The same, with system capabilities on the role, for a route gated on one.</summary>
    private async Task<(HttpClient Client, Guid UserId)> CallerHoldingAsync(
        string[] capabilities, params ContentTypePermission[] permissions)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Instructor_{Guid.NewGuid():n}",
            Permissions = [.. permissions],
            SystemCapabilities = [.. capabilities],
        };
        session.Store(role);

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"instructor_{Guid.NewGuid():n}",
            Email = $"instructor_{Guid.NewGuid():n}@example.com",
            RoleIds = [role.Id],
        };
        session.Store(user);
        await session.SaveChangesAsync();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: [role.Name], userId: user.Id.ToString()));

        return (client, user.Id);
    }

    private async Task<HttpClient> SuperAdminAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"admin_{Guid.NewGuid():n}",
            Email = $"admin_{Guid.NewGuid():n}@example.com",
            RoleIds = [SystemRoles.SuperAdminRoleId],
        };
        session.Store(user);
        await session.SaveChangesAsync();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: ["SuperAdmin"], userId: user.Id.ToString()));
        return client;
    }

    /// <summary>
    /// Stores entries directly, so a test can hold a shape the API would refuse on write. Each gets
    /// its own creation time: the list orders by it, and a tie would make a page boundary a guess.
    /// </summary>
    private async Task<List<Guid>> StoreAsync(
        string type,
        IEnumerable<Dictionary<string, object>> entries,
        SensitivityLevel sensitivity = SensitivityLevel.Public,
        string? tenant = null)
    {
        using var scope = _factory.Services.CreateScope();

        await using var session = tenant is null
            ? scope.ServiceProvider.GetRequiredService<IDocumentStore>().LightweightSession()
            : scope.ServiceProvider.GetRequiredService<IDocumentStore>().LightweightSession(tenant);

        var ids = new List<Guid>();

        foreach (var data in entries)
        {
            var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                .AddSeconds(Interlocked.Increment(ref _sequence));

            var content = new Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = ContentStatus.Published,
                Sensitivity = sensitivity,
                CreatedAt = created,
                UpdatedAt = created,
                Data = data,
            };

            session.Store(content);
            ids.Add(content.Id);
        }

        await session.SaveChangesAsync();
        return ids;
    }

    private async Task<Guid> ClassAsync(string type, Guid? instructor, string title = "a class")
    {
        var data = new Dictionary<string, object> { ["Title"] = title };
        if (instructor is { } id) data["InstructorUser"] = id.ToString();

        return (await StoreAsync(type, [data])).Single();
    }

    private Task<List<Guid>> EnrollAsync(string type, object? @class, int count = 1) =>
        StoreAsync(type, Enumerable.Range(0, count).Select(i =>
        {
            var data = new Dictionary<string, object> { ["Student"] = $"student {i}" };
            if (@class is not null) data["Class"] = @class;
            return data;
        }));

    private async Task EraseAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        session.Delete<Content>(id);
        await session.SaveChangesAsync();
    }

    private static async Task<Guid> CreateAsync(HttpClient admin, string type, Dictionary<string, object> data)
    {
        var res = await admin.PostAsJsonAsync("/api/contents", new { contentType = type, data });
        res.IsSuccessStatusCode.Should().BeTrue("creating an entry returned {0}: {1}",
            res.StatusCode, await res.Content.ReadAsStringAsync());

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Everything the caller can list, read page by page, with the total every page reported.
    /// </summary>
    private static async Task<(List<Guid> Ids, int Total)> ListedAsync(HttpClient client, string? type, int pageSize = 100)
    {
        var ids = new List<Guid>();
        var total = -1;

        for (var page = 1; page <= 40; page++)
        {
            var res = await client.GetAsync(
                $"/api/contents?page={page}&pageSize={pageSize}" + (type is null ? string.Empty : $"&contentType={type}"));
            res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var items = doc.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("id").GetGuid())
                .ToList();

            var reported = doc.RootElement.GetProperty("totalItems").GetInt32();
            if (total >= 0)
                reported.Should().Be(total, "page {0} counts the same entries as the pages before it", page);
            total = reported;

            ids.AddRange(items);

            if (items.Count < pageSize) break;
        }

        return (ids, total);
    }

    /// <summary>
    /// The total as well as the rows. The per-entry check over a page drops a row the query let
    /// through, so an empty page alone does not show the query selected nothing.
    /// </summary>
    private static async Task ShouldListNothingAsync(HttpClient client, string type) =>
        await ShouldListCountAsync(client, type, 0);

    private static async Task ShouldListCountAsync(HttpClient client, string type, int count)
    {
        var listed = await ListedAsync(client, type);

        listed.Ids.Should().HaveCount(count);
        listed.Total.Should().Be(count, "the total is counted by the query, before the per-entry check");
    }

    private static async Task<HttpStatusCode> GetAsync(HttpClient client, Guid id) =>
        (await client.GetAsync($"/api/contents/{id}")).StatusCode;

    /// <summary>
    /// The caller, the two types, a class of theirs with <paramref name="mine"/> enrollments and
    /// somebody else's class with <paramref name="theirs"/>.
    /// </summary>
    private async Task<Scene> SceneAsync(int mine = 2, int theirs = 2)
    {
        var (classes, enrollments) = await TypesAsync();
        var (client, userId) = await CallerAsync(Classes(classes), Enrollments(enrollments));

        var myClass = await ClassAsync(classes, userId);
        var otherClass = await ClassAsync(classes, Guid.NewGuid());

        var own = await EnrollAsync(enrollments, myClass.ToString(), mine);
        var others = await EnrollAsync(enrollments, otherClass.ToString(), theirs);

        return new Scene(client, userId, classes, enrollments, myClass, otherClass, own, others);
    }

    private sealed record Scene(
        HttpClient Client,
        Guid UserId,
        string Classes,
        string Enrollments,
        Guid MyClass,
        Guid OtherClass,
        List<Guid> Mine,
        List<Guid> Theirs);

    [Fact]
    public async Task An_instructor_reads_an_enrollment_of_their_class_and_is_refused_another_classs()
    {
        var scene = await SceneAsync();

        (await GetAsync(scene.Client, scene.Mine[0])).Should().Be(HttpStatusCode.OK);
        (await GetAsync(scene.Client, scene.Theirs[0])).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_list_pages_and_counts_only_the_enrollments_of_the_instructors_classes()
    {
        var scene = await SceneAsync(mine: 5, theirs: 4);

        var (ids, total) = await ListedAsync(scene.Client, scene.Enrollments, pageSize: 2);

        total.Should().Be(5, "the total counts what the instructor may read, on every page");
        ids.Should().HaveCount(5, "three pages of two hold all five and nothing else");
        ids.Should().BeEquivalentTo(scene.Mine);
    }

    [Fact]
    public async Task A_list_the_database_cannot_page_holds_only_the_instructors_enrollments()
    {
        // A number inside a list is a shape the predicate compiler declines, so this rule is
        // answered per entry. The extra condition is true for every entry here.
        var (classes, enrollments) = await TypesAsync();

        var rule = Teaches();
        rule.Conditions!["Student"] = new Dictionary<string, object> { ["_nin"] = new List<object> { 42L } };

        var (client, userId) = await CallerAsync(
            Classes(classes), new ContentTypePermission { ContentTypeSlug = enrollments, Read = rule });

        var mine = await EnrollAsync(enrollments, (await ClassAsync(classes, userId)).ToString(), 5);
        await EnrollAsync(enrollments, (await ClassAsync(classes, Guid.NewGuid())).ToString(), 4);

        var (ids, total) = await ListedAsync(client, enrollments, pageSize: 2);

        total.Should().Be(5);
        ids.Should().HaveCount(5);
        ids.Should().BeEquivalentTo(mine);
    }

    [Fact]
    public async Task A_list_of_every_type_holds_the_instructors_class_and_its_enrollments()
    {
        // With no contentType the list asks for no predicate and puts every entry through the
        // per-entry check. This caller's role reads these two types and nothing else.
        var scene = await SceneAsync(mine: 3, theirs: 2);

        var (ids, total) = await ListedAsync(scene.Client, null);

        total.Should().Be(4);
        ids.Should().HaveCount(4);
        ids.Should().BeEquivalentTo(scene.Mine.Append(scene.MyClass));
    }

    [Fact]
    public async Task An_update_is_allowed_only_on_an_enrollment_of_the_instructors_class()
    {
        var (classes, enrollments) = await TypesAsync();
        var (client, userId) = await CallerAsync(Classes(classes), Enrollments(enrollments));
        var admin = await SuperAdminAsync();

        var myClass = await ClassAsync(classes, userId);
        var otherClass = await ClassAsync(classes, Guid.NewGuid());

        var mine = await CreateAsync(admin, enrollments,
            new() { ["Student"] = "ana", ["Class"] = myClass.ToString() });
        var theirs = await CreateAsync(admin, enrollments,
            new() { ["Student"] = "ben", ["Class"] = otherClass.ToString() });

        var refused = await client.PutAsJsonAsync($"/api/contents/{theirs}", new
        {
            id = theirs,
            data = new Dictionary<string, object> { ["Student"] = "taken over", ["Class"] = otherClass.ToString() },
        });
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var allowed = await client.PutAsJsonAsync($"/api/contents/{mine}", new
        {
            id = mine,
            data = new Dictionary<string, object> { ["Student"] = "ana maria", ["Class"] = myClass.ToString() },
        });
        allowed.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            allowed.StatusCode, await allowed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_transition_is_allowed_only_on_an_enrollment_of_the_instructors_class()
    {
        var (classes, enrollments) = await TypesAsync(new LifecycleDefinition
        {
            States = ["Pending", "Confirmed"],
            InitialState = "Pending",
            Transitions = [new StateTransition { Name = "Confirm", From = "Pending", To = "Confirmed" }],
        });

        // Read is unconditional, so a refusal below is the transition rule and not the read floor
        // the endpoint checks first.
        var (client, userId) = await CallerAsync(Classes(classes), new ContentTypePermission
        {
            ContentTypeSlug = enrollments,
            Read = new PermissionRule { Enabled = true },
            Transitions = new(StringComparer.OrdinalIgnoreCase) { ["Confirm"] = Teaches() },
        });
        var admin = await SuperAdminAsync();

        var myClass = await ClassAsync(classes, userId);
        var otherClass = await ClassAsync(classes, Guid.NewGuid());

        var mine = await CreateAsync(admin, enrollments,
            new() { ["Student"] = "ana", ["Class"] = myClass.ToString() });
        var theirs = await CreateAsync(admin, enrollments,
            new() { ["Student"] = "ben", ["Class"] = otherClass.ToString() });

        var refused = await client.PutAsJsonAsync(
            $"/api/contents/{theirs}/status", new { id = theirs, transition = "Confirm" });
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var allowed = await client.PutAsJsonAsync(
            $"/api/contents/{mine}/status", new { id = mine, transition = "Confirm" });
        allowed.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            allowed.StatusCode, await allowed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_class_the_instructor_may_not_read_grants_nothing()
    {
        var (classes, enrollments) = await TypesAsync();

        // No rule on the class type at all, then one that leaves the class out.
        var (unread, unreadId) = await CallerAsync(Enrollments(enrollments));
        var (partial, partialId) = await CallerAsync(
            new ContentTypePermission { ContentTypeSlug = classes, Read = Where("Title", "_eq", "open") },
            Enrollments(enrollments));

        var unreadClass = await ClassAsync(classes, unreadId, "open");
        var closedClass = await ClassAsync(classes, partialId, "closed");
        var openClass = await ClassAsync(classes, partialId, "open");

        var inUnread = (await EnrollAsync(enrollments, unreadClass.ToString())).Single();
        var inClosed = (await EnrollAsync(enrollments, closedClass.ToString())).Single();
        var inOpen = (await EnrollAsync(enrollments, openClass.ToString())).Single();

        (await GetAsync(unread, inUnread)).Should().Be(HttpStatusCode.Forbidden,
            "they teach the class, and their role does not read classes");
        await ShouldListNothingAsync(unread, enrollments);

        (await GetAsync(partial, inClosed)).Should().Be(HttpStatusCode.Forbidden,
            "they teach the class, and their own rule for classes leaves it out");
        (await GetAsync(partial, inOpen)).Should().Be(HttpStatusCode.OK, "the class they may read is the control");

        var listed = await ListedAsync(partial, enrollments);
        listed.Ids.Should().HaveCount(1);
        listed.Total.Should().Be(1);
        listed.Ids.Should().Equal(inOpen);
    }

    [Fact]
    public async Task A_reference_to_an_erased_class_grants_nothing()
    {
        var scene = await SceneAsync();
        var doomedClass = await ClassAsync(scene.Classes, scene.UserId);
        var orphan = (await EnrollAsync(scene.Enrollments, doomedClass.ToString())).Single();

        (await GetAsync(scene.Client, orphan)).Should().Be(HttpStatusCode.OK, "the class is still there");
        await ShouldListCountAsync(scene.Client, scene.Enrollments, 3);

        await EraseAsync(doomedClass);

        (await GetAsync(scene.Client, orphan)).Should().Be(HttpStatusCode.Forbidden);

        var listed = await ListedAsync(scene.Client, scene.Enrollments);
        listed.Ids.Should().HaveCount(2);
        listed.Total.Should().Be(2);
        listed.Ids.Should().BeEquivalentTo(scene.Mine);
    }

    [Fact]
    public async Task A_reference_to_a_class_in_another_tenant_grants_nothing()
    {
        var scene = await SceneAsync();
        var elsewhere = $"other-{Guid.NewGuid():n}"[..16];

        // Taught by the caller, of the same type, and stored in another tenant's partition.
        var foreignClass = (await StoreAsync(
            scene.Classes,
            [new() { ["Title"] = "elsewhere", ["InstructorUser"] = scene.UserId.ToString() }],
            tenant: elsewhere)).Single();
        try
        {
            var pointsAway = (await EnrollAsync(scene.Enrollments, foreignClass.ToString())).Single();

            (await GetAsync(scene.Client, pointsAway)).Should().Be(HttpStatusCode.Forbidden);
            (await GetAsync(scene.Client, scene.Mine[0])).Should().Be(HttpStatusCode.OK, "a class in this tenant is the control");

            var listed = await ListedAsync(scene.Client, scene.Enrollments);
            listed.Ids.Should().HaveCount(2);
            listed.Total.Should().Be(2);
            listed.Ids.Should().BeEquivalentTo(scene.Mine);

            var everyType = await ListedAsync(scene.Client, null);
            everyType.Ids.Should().HaveCount(3);
            everyType.Ids.Should().NotContain(pointsAway);
        }
        finally
        {
            // The only row under that tenant id, so the partition does not outlive the test for
            // the passes that visit every tenant with rows.
            using var scope = _factory.Services.CreateScope();
            await using var session = scope.ServiceProvider.GetRequiredService<IDocumentStore>().LightweightSession(elsewhere);
            session.Delete<Content>(foreignClass);
            await session.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task An_enrollment_whose_class_is_missing_or_is_not_an_id_grants_nothing()
    {
        var scene = await SceneAsync();

        var refused = new List<Guid>();
        refused.AddRange(await EnrollAsync(scene.Enrollments, null));
        refused.AddRange(await EnrollAsync(scene.Enrollments, "not an id"));
        refused.AddRange(await EnrollAsync(scene.Enrollments, 42L));
        refused.AddRange(await EnrollAsync(scene.Enrollments, string.Empty));
        // The caller's class, written without hyphens. The list matches the hyphenated form, so a
        // single read that accepted this one would open an entry the list never shows.
        refused.AddRange(await EnrollAsync(scene.Enrollments, scene.MyClass.ToString("N")));
        refused.AddRange(await EnrollAsync(scene.Enrollments, new List<object> { scene.MyClass.ToString() }));

        // A class of the caller's whose id starts with zeros, and that id respelled with a prefix
        // the id parser takes in place of leading digits. The text is not the id the list compares.
        var zeros = Guid.Parse("00" + Guid.NewGuid().ToString()[2..]);
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Content
            {
                Id = zeros,
                ContentType = scene.Classes,
                Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Public,
                Data = new Dictionary<string, object> { ["Title"] = "zeros", ["InstructorUser"] = scene.UserId.ToString() },
            });
            await session.SaveChangesAsync();
        }

        refused.AddRange(await EnrollAsync(scene.Enrollments, "0x" + zeros.ToString()[2..]));
        refused.AddRange(await EnrollAsync(scene.Enrollments, "+0" + zeros.ToString()[2..]));

        refused.Should().HaveCount(8);
        foreach (var id in refused)
            (await GetAsync(scene.Client, id)).Should().Be(HttpStatusCode.Forbidden);

        // Upper case is the same id, and both paths read it. So is the zeros class spelled plainly,
        // which shows the two respellings above were refused for their spelling.
        var upper = (await EnrollAsync(scene.Enrollments, scene.MyClass.ToString().ToUpperInvariant())).Single();
        var plain = (await EnrollAsync(scene.Enrollments, zeros.ToString())).Single();
        (await GetAsync(scene.Client, upper)).Should().Be(HttpStatusCode.OK);
        (await GetAsync(scene.Client, plain)).Should().Be(HttpStatusCode.OK);

        var listed = await ListedAsync(scene.Client, scene.Enrollments);
        listed.Ids.Should().HaveCount(4);
        listed.Total.Should().Be(4);
        listed.Ids.Should().BeEquivalentTo(scene.Mine.Append(upper).Append(plain));
    }

    [Fact]
    public async Task A_reference_field_whose_type_changed_grants_nothing()
    {
        var scene = await SceneAsync();

        (await GetAsync(scene.Client, scene.Mine[0])).Should().Be(HttpStatusCode.OK, "the field is a reference so far");

        // No longer a reference. The stored values are still class ids.
        await ChangeTypeAsync(scene.Enrollments, definition =>
        {
            var field = definition.Fields.Single(f => f.Name == "Class");
            field.Type = "string";
            field.ReferenceType = null;
        });

        (await GetAsync(scene.Client, scene.Mine[0])).Should().Be(HttpStatusCode.Forbidden);
        await ShouldListNothingAsync(scene.Client, scene.Enrollments);

        // A reference again, to a type the stored ids are not entries of.
        var (otherClasses, _) = await TypesAsync();
        await ChangeTypeAsync(scene.Enrollments, definition =>
        {
            var field = definition.Fields.Single(f => f.Name == "Class");
            field.Type = "reference";
            field.ReferenceType = otherClasses;
        });

        (await GetAsync(scene.Client, scene.Mine[0])).Should().Be(HttpStatusCode.Forbidden);
        await ShouldListNothingAsync(scene.Client, scene.Enrollments);

        // Back to what it was, which is the control for both changes.
        await ChangeTypeAsync(scene.Enrollments, definition =>
            definition.Fields.Single(f => f.Name == "Class").ReferenceType = scene.Classes);

        (await GetAsync(scene.Client, scene.Mine[0])).Should().Be(HttpStatusCode.OK);
        await ShouldListCountAsync(scene.Client, scene.Enrollments, 2);
    }

    [Fact]
    public async Task A_class_without_the_field_grants_nothing_even_under_not_equal()
    {
        var (classes, enrollments) = await TypesAsync();

        // "Taught by anyone but this stranger" is true of every class that names an instructor.
        var (client, _) = await CallerAsync(
            new ContentTypePermission { ContentTypeSlug = classes, Read = new PermissionRule { Enabled = true } },
            new ContentTypePermission
            {
                ContentTypeSlug = enrollments,
                Read = Where(Follows, "_ne", Guid.NewGuid().ToString()),
            });

        var staffed = await ClassAsync(classes, Guid.NewGuid());
        var unstaffed = await ClassAsync(classes, instructor: null);

        var inStaffed = (await EnrollAsync(enrollments, staffed.ToString())).Single();
        var inUnstaffed = (await EnrollAsync(enrollments, unstaffed.ToString())).Single();
        var nowhere = (await EnrollAsync(enrollments, null)).Single();

        (await GetAsync(client, inStaffed)).Should().Be(HttpStatusCode.OK, "a class with an instructor is the control");
        (await GetAsync(client, inUnstaffed)).Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync(client, nowhere)).Should().Be(HttpStatusCode.Forbidden);

        var listed = await ListedAsync(client, enrollments);
        listed.Ids.Should().HaveCount(1);
        listed.Total.Should().Be(1);
        listed.Ids.Should().Equal(inStaffed);
    }

    [Fact]
    public async Task A_field_that_is_not_public_on_the_class_grants_nothing()
    {
        var scene = await SceneAsync();

        (await GetAsync(scene.Client, scene.Mine[0])).Should().Be(HttpStatusCode.OK, "the field is Public so far");

        await ChangeTypeAsync(scene.Classes, definition =>
            definition.Fields.Single(f => f.Name == "InstructorUser").Sensitivity = SensitivityLevel.Sensitive);

        (await GetAsync(scene.Client, scene.Mine[0])).Should().Be(HttpStatusCode.Forbidden);
        await ShouldListNothingAsync(scene.Client, scene.Enrollments);

        // The class's own rule reads the same field off the row itself, and is as it was.
        (await GetAsync(scene.Client, scene.MyClass)).Should().Be(HttpStatusCode.OK);

        await ChangeTypeAsync(scene.Classes, definition =>
            definition.Fields.RemoveAll(f => f.Name == "InstructorUser"));

        (await GetAsync(scene.Client, scene.Mine[0])).Should().Be(HttpStatusCode.Forbidden,
            "the class type no longer declares the field, whatever its entries still hold");
        await ShouldListNothingAsync(scene.Client, scene.Enrollments);
    }

    [Fact]
    public async Task A_class_that_is_not_public_grants_nothing()
    {
        var scene = await SceneAsync();

        var guarded = (await StoreAsync(
            scene.Classes,
            [new() { ["Title"] = "guarded", ["InstructorUser"] = scene.UserId.ToString() }],
            SensitivityLevel.Sensitive)).Single();
        var inGuarded = (await EnrollAsync(scene.Enrollments, guarded.ToString())).Single();

        (await GetAsync(scene.Client, inGuarded)).Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync(scene.Client, scene.Mine[0])).Should().Be(HttpStatusCode.OK, "a Public class is the control");

        var listed = await ListedAsync(scene.Client, scene.Enrollments);
        listed.Ids.Should().HaveCount(2);
        listed.Total.Should().Be(2);
        listed.Ids.Should().BeEquivalentTo(scene.Mine);
    }

    [Fact]
    public async Task A_key_written_into_the_enrollment_itself_grants_nothing()
    {
        var scene = await SceneAsync();

        // Somebody else's class, with the rule's own key forged onto the row.
        var forged = (await StoreAsync(scene.Enrollments,
        [
            new()
            {
                ["Student"] = "forger",
                ["Class"] = scene.OtherClass.ToString(),
                [Follows] = scene.UserId.ToString(),
            },
        ])).Single();

        (await GetAsync(scene.Client, forged)).Should().Be(HttpStatusCode.Forbidden);

        var listed = await ListedAsync(scene.Client, scene.Enrollments);
        listed.Ids.Should().HaveCount(2);
        listed.Total.Should().Be(2);
        listed.Ids.Should().NotContain(forged);

        (await ListedAsync(scene.Client, null)).Ids.Should().NotContain(forged);
    }

    [Fact]
    public async Task A_class_whose_own_rule_follows_a_reference_grants_nothing_to_an_enrollment()
    {
        var suffix = Guid.NewGuid().ToString("n")[..10];
        var departments = $"refdept{suffix}";
        var classes = $"refclass{suffix}";
        var enrollments = $"refenrol{suffix}";

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = departments, DisplayName = departments,
                Fields = [new FieldDefinition { Name = "Head", DisplayName = "Head", Type = "string" }],
            });
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = classes, DisplayName = classes,
                Fields =
                [
                    new FieldDefinition { Name = "InstructorUser", DisplayName = "Instructor", Type = "string" },
                    new FieldDefinition
                    {
                        Name = "Department", DisplayName = "Department", Type = "reference", ReferenceType = departments,
                    },
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

        var everyDepartment = new ContentTypePermission
        {
            ContentTypeSlug = departments,
            Read = new PermissionRule { Enabled = true },
        };

        // Reads a class through its department, and an enrollment through its class: two hops.
        var (head, headId) = await CallerAsync(
            everyDepartment,
            new ContentTypePermission
            {
                ContentTypeSlug = classes,
                Read = Where("Department.Head", "_eq", "$CURRENT_USER"),
            },
            Enrollments(enrollments));

        var department = (await StoreAsync(departments, [new() { ["Head"] = headId.ToString() }])).Single();
        var taught = (await StoreAsync(classes,
        [
            new() { ["InstructorUser"] = headId.ToString(), ["Department"] = department.ToString() },
        ])).Single();
        var enrollment = (await EnrollAsync(enrollments, taught.ToString())).Single();

        (await GetAsync(head, taught)).Should().Be(HttpStatusCode.OK, "one reference is followed, and this is one");
        (await GetAsync(head, enrollment)).Should().Be(HttpStatusCode.Forbidden, "this would be two");
        await ShouldListNothingAsync(head, enrollments);

        var classesListed = await ListedAsync(head, classes);
        classesListed.Ids.Should().Equal(taught);
        classesListed.Total.Should().Be(1);

        // The control: the same entries, read by a caller whose rule for classes follows nothing.
        var (instructor, instructorId) = await CallerAsync(Classes(classes), Enrollments(enrollments));
        var theirClass = (await StoreAsync(classes,
        [
            new() { ["InstructorUser"] = instructorId.ToString(), ["Department"] = department.ToString() },
        ])).Single();
        var theirEnrollment = (await EnrollAsync(enrollments, theirClass.ToString())).Single();

        (await GetAsync(instructor, theirEnrollment)).Should().Be(HttpStatusCode.OK);

        var theirs = await ListedAsync(instructor, enrollments);
        theirs.Ids.Should().Equal(theirEnrollment);
        theirs.Total.Should().Be(1);
    }

    [Fact]
    public async Task The_list_and_the_single_read_agree_on_every_enrollment()
    {
        var scene = await SceneAsync(mine: 3, theirs: 3);

        var all = new List<Guid>(scene.Mine.Concat(scene.Theirs));
        all.AddRange(await EnrollAsync(scene.Enrollments, null));
        all.AddRange(await EnrollAsync(scene.Enrollments, "not an id"));
        all.AddRange(await EnrollAsync(scene.Enrollments, scene.MyClass.ToString("N")));
        all.AddRange(await EnrollAsync(scene.Enrollments, scene.MyClass.ToString().ToUpperInvariant()));
        all.AddRange(await EnrollAsync(scene.Enrollments, Guid.NewGuid().ToString()));

        var erased = await ClassAsync(scene.Classes, scene.UserId);
        all.AddRange(await EnrollAsync(scene.Enrollments, erased.ToString()));
        await EraseAsync(erased);

        all.Should().HaveCount(12);

        var listed = (await ListedAsync(scene.Client, scene.Enrollments, pageSize: 5)).Ids;
        listed.Should().HaveCount(4, "the three in their class and the one that names it in upper case");

        foreach (var id in all)
        {
            var opened = await GetAsync(scene.Client, id) == HttpStatusCode.OK;
            opened.Should().Be(listed.Contains(id), "entry {0} must be listed exactly when it can be opened", id);
        }
    }

    [Fact]
    public async Task A_stored_condition_the_api_would_refuse_grants_nothing()
    {
        var (classes, enrollments) = await TypesAsync();

        // Two hops, a field that is not a reference, and no operator: none can be saved through
        // the API, and a role stored before the check may hold any of them.
        var (twoHops, twoHopsId) = await CallerAsync(Classes(classes), new ContentTypePermission
        {
            ContentTypeSlug = enrollments,
            Read = Where("Class.Teacher.InstructorUser", "_ne", "nobody"),
        });
        var (notAReference, _) = await CallerAsync(Classes(classes), new ContentTypePermission
        {
            ContentTypeSlug = enrollments,
            Read = Where("Student.InstructorUser", "_ne", "nobody"),
        });
        var (noOperator, noOperatorId) = await CallerAsync(Classes(classes), new ContentTypePermission
        {
            ContentTypeSlug = enrollments,
            Read = new PermissionRule
            {
                Enabled = true,
                Conditions = new Dictionary<string, object> { [Follows] = new Dictionary<string, object>() },
            },
        });

        var first = (await EnrollAsync(enrollments, (await ClassAsync(classes, twoHopsId)).ToString())).Single();
        var second = (await EnrollAsync(enrollments, (await ClassAsync(classes, noOperatorId)).ToString())).Single();

        foreach (var client in new[] { twoHops, notAReference, noOperator })
        {
            (await GetAsync(client, first)).Should().Be(HttpStatusCode.Forbidden);
            (await GetAsync(client, second)).Should().Be(HttpStatusCode.Forbidden);
            await ShouldListNothingAsync(client, enrollments);
        }

        // The control: the entries are readable under a rule that does resolve.
        var (instructor, instructorId) = await CallerAsync(Classes(classes), Enrollments(enrollments));
        var theirs = (await EnrollAsync(enrollments, (await ClassAsync(classes, instructorId)).ToString())).Single();
        (await GetAsync(instructor, theirs)).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_single_field_condition_reads_as_it_did()
    {
        var scene = await SceneAsync();

        (await GetAsync(scene.Client, scene.MyClass)).Should().Be(HttpStatusCode.OK);
        (await GetAsync(scene.Client, scene.OtherClass)).Should().Be(HttpStatusCode.Forbidden);

        var listed = await ListedAsync(scene.Client, scene.Classes);
        listed.Ids.Should().HaveCount(1);
        listed.Total.Should().Be(1);
        listed.Ids.Should().Equal(scene.MyClass);
    }

    [Fact]
    public async Task Two_conditions_that_follow_a_reference_hold_together()
    {
        var (classes, enrollments) = await TypesAsync();

        var rule = Teaches();
        rule.Conditions!["Class.Title"] = new Dictionary<string, object> { ["_eq"] = "algebra" };

        var (client, userId) = await CallerAsync(
            Classes(classes), new ContentTypePermission { ContentTypeSlug = enrollments, Read = rule });

        var myAlgebra = await ClassAsync(classes, userId, "algebra");
        var myBiology = await ClassAsync(classes, userId, "biology");
        var theirAlgebra = await ClassAsync(classes, Guid.NewGuid(), "algebra");

        var granted = await EnrollAsync(enrollments, myAlgebra.ToString(), 2);
        var wrongTitle = (await EnrollAsync(enrollments, myBiology.ToString())).Single();
        var wrongTeacher = (await EnrollAsync(enrollments, theirAlgebra.ToString())).Single();

        (await GetAsync(client, granted[0])).Should().Be(HttpStatusCode.OK);
        (await GetAsync(client, wrongTitle)).Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync(client, wrongTeacher)).Should().Be(HttpStatusCode.Forbidden);

        var listed = await ListedAsync(client, enrollments);
        listed.Ids.Should().HaveCount(2);
        listed.Total.Should().Be(2);
        listed.Ids.Should().BeEquivalentTo(granted);

        // The list of every type is the per-entry pass, where each condition is asked of each row.
        var everyType = await ListedAsync(client, null);
        everyType.Ids.Should().HaveCount(4);
        everyType.Ids.Should().BeEquivalentTo(granted.Append(myAlgebra).Append(myBiology));
    }

    [Fact]
    public async Task Other_peoples_rows_do_not_use_up_what_a_pass_over_every_type_may_read()
    {
        var (classes, enrollments) = await TypesAsync();
        var (client, userId) = await CallerAsync(Classes(classes), Enrollments(enrollments));

        try
        {
            // The caller's one enrollment is the oldest row, so a pass in creation order meets
            // every other instructor's row first.
            var myClass = await ClassAsync(classes, userId);
            var mine = (await EnrollAsync(enrollments, myClass.ToString())).Single();

            var stranger = Guid.NewGuid();
            var theirClasses = await StoreAsync(classes, Enumerable.Range(0, ReferenceConditions.MaxEntriesPerRequest)
                .Select(i => new Dictionary<string, object> { ["Title"] = $"class {i}", ["InstructorUser"] = stranger.ToString() }));
            var theirs = await StoreAsync(enrollments, theirClasses
                .Select(id => new Dictionary<string, object> { ["Student"] = "somebody else", ["Class"] = id.ToString() }));
            theirs.Should().HaveCount(ReferenceConditions.MaxEntriesPerRequest);

            // No contentType, so every entry goes through the per-entry check. The caller reads
            // two rows in the whole database: their class and their enrollment.
            var listed = await ListedAsync(client, null);

            listed.Ids.Should().HaveCount(2);
            listed.Ids.Should().BeEquivalentTo(new[] { myClass, mine });
            listed.Total.Should().Be(2);
        }
        finally
        {
            await PurgeAsync(classes, enrollments);
        }
    }

    [Fact]
    public async Task A_list_answered_per_entry_over_more_classes_than_a_request_loads_one_by_one_holds_them_all()
    {
        var (classes, enrollments) = await TypesAsync();

        // The number in a list keeps the rule out of the database, so the list is the per-entry
        // pass, and each enrollment here points at a class of its own.
        var rule = Teaches();
        rule.Conditions!["Student"] = new Dictionary<string, object> { ["_nin"] = new List<object> { 42L } };

        var (client, userId) = await CallerAsync(
            Classes(classes), new ContentTypePermission { ContentTypeSlug = enrollments, Read = rule });

        var count = ReferenceConditions.MaxEntriesPerRequest + 1;

        try
        {
            var taught = await StoreAsync(classes, Enumerable.Range(0, count)
                .Select(i => new Dictionary<string, object> { ["Title"] = $"class {i}", ["InstructorUser"] = userId.ToString() }));

            var enrolled = await StoreAsync(enrollments, taught
                .Select(id => new Dictionary<string, object> { ["Student"] = "a student", ["Class"] = id.ToString() }));
            enrolled.Should().HaveCount(count);

            var listed = await ListedAsync(client, enrollments);

            listed.Total.Should().Be(count);
            listed.Ids.Should().HaveCount(count);
            listed.Ids.Should().BeEquivalentTo(enrolled);
        }
        finally
        {
            await PurgeAsync(classes, enrollments);
        }
    }

    private const string BoundClasses = "refboundclass";
    private const string BoundEnrollments = "refboundenrol";
    private const string BoundPages = "refboundpage";

    private static readonly object TreeGate = new();
    private static WebApplicationFactory<Program>? _treeHost;

    /// <summary>
    /// A host whose page tree reads <see cref="BoundPages"/>. Kept for the run and never disposed,
    /// as the host in <c>PagesModuleTests</c> is.
    /// </summary>
    private WebApplicationFactory<Program> TreeHost()
    {
        lock (TreeGate)
        {
            return _treeHost ??= _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.Configure<BarakoCMS.Pages.PagesOptions>(o => o.ContentType = BoundPages)));
        }
    }

    /// <summary>
    /// Fixed names, because the page tree's type is set when its host is built. A class, an
    /// enrollment with a slug so it can be pushed, and a page owned by a class.
    /// </summary>
    private async Task EnsureBoundTypesAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        if (await session.Query<ContentTypeDefinition>().AnyAsync(d => d.Name == BoundClasses))
            return;

        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = BoundClasses, DisplayName = BoundClasses,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "InstructorUser", DisplayName = "Instructor", Type = "string" },
            ],
        });

        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = BoundEnrollments, DisplayName = BoundEnrollments,
            Fields =
            [
                new FieldDefinition { Name = "Student", DisplayName = "Student", Type = "string" },
                new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" },
                new FieldDefinition { Name = "Class", DisplayName = "Class", Type = "reference", ReferenceType = BoundClasses },
            ],
        });

        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = BoundPages, DisplayName = BoundPages,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" },
                new FieldDefinition { Name = "Owner", DisplayName = "Owner", Type = "reference", ReferenceType = BoundClasses },
            ],
        });

        await session.SaveChangesAsync();
    }

    /// <summary>
    /// A 403 that says why: the reason names the condition and the bound, so the caller is told
    /// what a row rule's bare 403 does not tell them.
    /// </summary>
    private static async Task ShouldBeRefusedAsync(Task<HttpResponseMessage> request, string pass, string key)
    {
        var response = await request;
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "{0} answered {1}", pass, body);

        var reasons = new List<string>();
        using (var doc = JsonDocument.Parse(body))
            Collect(doc.RootElement, reasons);

        var bound = $"more than {ReferenceConditions.MaxEntriesPerCondition} entries";

        reasons.Should().Contain(
            reason => reason.Contains(key) && reason.Contains(bound),
            "{0} says which condition and what bound, and answered {1}", pass, body);
    }

    private static void Collect(JsonElement element, List<string> texts)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    Collect(property.Value, texts);
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Collect(item, texts);
                break;

            case JsonValueKind.String:
                texts.Add(element.GetString() ?? string.Empty);
                break;
        }
    }

    /// <summary>
    /// One set of 1,002 classes for every pass, since storing them is most of what this costs:
    /// 1,156 rows stored in five writes, 12 requests, one extra host for the page tree.
    /// </summary>
    [Fact]
    public async Task A_condition_leading_to_more_entries_than_a_set_holds_pages_a_named_type_and_refuses_every_other_pass()
    {
        await EnsureBoundTypesAsync();
        await PurgeAsync(BoundClasses, BoundEnrollments, BoundPages);

        // Teaches more classes than a set holds. The database can answer their rules whole.
        var (client, userId) = await CallerHoldingAsync(
            ["export_content"],
            Classes(BoundClasses),
            Enrollments(BoundEnrollments),
            new ContentTypePermission { ContentTypeSlug = BoundPages, Read = Where("Owner.InstructorUser", "_eq", "$CURRENT_USER") });

        // Reads every published class, by a rule the database cannot answer, and the enrollments
        // of any class that has a title.
        var (reader, _) = await CallerAsync(
            new ContentTypePermission { ContentTypeSlug = BoundClasses, Read = Where("$status", "_eq", "Published") },
            new ContentTypePermission { ContentTypeSlug = BoundEnrollments, Read = Where("Class.Title", "_ne", "no such title") });

        try
        {
            var taught = await StoreAsync(BoundClasses, Enumerable.Range(0, ReferenceConditions.MaxEntriesPerCondition + 1)
                .Select(i => new Dictionary<string, object> { ["Title"] = $"class {i}", ["InstructorUser"] = userId.ToString() }));
            taught.Should().HaveCount(ReferenceConditions.MaxEntriesPerCondition + 1);

            var notTheirs = await ClassAsync(BoundClasses, Guid.NewGuid());

            // More than a page of enrollments, each in a class of its own. The first names its
            // class in upper case, which the subquery has to read as the list of ids does.
            var mine = await StoreAsync(BoundEnrollments, taught.Take(150).Select((id, i) => new Dictionary<string, object>
            {
                ["Student"] = "a student",
                ["Slug"] = $"bound-{i}",
                ["Class"] = i == 0 ? id.ToString().ToUpperInvariant() : id.ToString(),
            }));
            mine.Should().HaveCount(150);

            var theirs = (await StoreAsync(BoundEnrollments,
            [
                new() { ["Student"] = "somebody else", ["Slug"] = "bound-other", ["Class"] = notTheirs.ToString() },
                new() { ["Student"] = "nowhere", ["Slug"] = "bound-nowhere", ["Class"] = "not an id" },
            ]));

            await StoreAsync(BoundPages, taught.Take(2).Select((id, i) => new Dictionary<string, object>
            {
                ["Title"] = $"page {i}",
                ["Slug"] = $"bound-page-{i}",
                ["Owner"] = id.ToString(),
            }));

            // A named type: the database filters by subquery, and pages and counts.
            var typed = await ListedAsync(client, BoundEnrollments);
            typed.Total.Should().Be(150);
            typed.Ids.Should().HaveCount(150);
            typed.Ids.Should().BeEquivalentTo(mine);

            // One entry: one read.
            (await GetAsync(client, mine[7])).Should().Be(HttpStatusCode.OK);
            (await GetAsync(client, theirs[0])).Should().Be(HttpStatusCode.Forbidden);

            // Every pass that walks rows itself is refused, with the reason.
            // The caller holds two conditions into classes, on enrollments and on pages, and the
            // refusal names whichever the pass met at its second row.
            await ShouldBeRefusedAsync(client.GetAsync("/api/contents?pageSize=10"), "the list of every type", ".InstructorUser");

            await ShouldBeRefusedAsync(
                client.GetAsync($"/api/portability/export?types={BoundEnrollments}"), "the export", Follows);

            await ShouldBeRefusedAsync(
                client.PostAsJsonAsync($"/api/collections/{BoundEnrollments}/push", new
                {
                    entries = new[]
                    {
                        new Dictionary<string, object> { ["Slug"] = "bound-0", ["Student"] = "pushed", ["Class"] = taught[0].ToString() },
                        new Dictionary<string, object> { ["Slug"] = "bound-1", ["Student"] = "pushed", ["Class"] = taught[1].ToString() },
                    },
                }),
                "the push",
                Follows);

            var tree = TreeHost().CreateClient();
            tree.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;
            await ShouldBeRefusedAsync(tree.GetAsync("/api/pages/tree"), "the page tree", "Owner.InstructorUser");

            // A named type whose condition the database cannot answer whole: refused too, before
            // the type is loaded.
            await ShouldBeRefusedAsync(
                reader.GetAsync($"/api/contents?contentType={BoundEnrollments}&pageSize=10"),
                "a named type under a read rule the database cannot answer",
                "Class.Title");

            // At a thousand each condition has a set again, and every pass answers.
            await EraseAsync(taught[^1]);
            await EraseAsync(notTheirs);

            var everyType = await client.GetAsync("/api/contents?pageSize=1");
            everyType.StatusCode.Should().Be(HttpStatusCode.OK, await everyType.Content.ReadAsStringAsync());
            using (var doc = JsonDocument.Parse(await everyType.Content.ReadAsStringAsync()))
            {
                doc.RootElement.GetProperty("totalItems").GetInt32().Should().Be(
                    ReferenceConditions.MaxEntriesPerCondition + 150 + 2,
                    "a thousand classes, their 150 enrollments and the two pages they own");
            }

            var readerTyped = await ListedAsync(reader, BoundEnrollments);
            readerTyped.Total.Should().Be(150);
            readerTyped.Ids.Should().HaveCount(150);
            readerTyped.Ids.Should().BeEquivalentTo(mine);
        }
        finally
        {
            await PurgeAsync(BoundClasses, BoundEnrollments, BoundPages);
        }
    }

    private async Task PurgeAsync(params string[] types)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        foreach (var type in types)
            session.DeleteWhere<Content>(c => c.ContentType == type);

        await session.SaveChangesAsync();
    }
}

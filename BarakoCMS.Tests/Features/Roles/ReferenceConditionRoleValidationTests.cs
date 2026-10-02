using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Roles;

/// <summary>
/// A condition that follows a reference is checked when the role is saved, on create and on
/// update: its shape, and what it names in the tenant's content types.
/// </summary>
/// <remarks>
/// A condition that cannot resolve denies when it is evaluated, so a refusal here protects nobody.
/// It tells whoever saves the role, in place of a role that reads nothing and does not say why.
///
/// Every case runs against <c>POST /api/roles</c> and <c>PUT /api/roles/{id}</c>, since the two
/// slices each carry the check.
/// </remarks>
[Collection("Sequential")]
public class ReferenceConditionRoleValidationTests
{
    private readonly IntegrationTestFixture _fixture;

    public ReferenceConditionRoleValidationTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private async Task<HttpClient> AdminAsync()
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin"));
        return client;
    }

    private async Task<(string Classes, string Enrollments)> TypesAsync()
    {
        var suffix = Guid.NewGuid().ToString("n")[..10];
        var classes = $"valclass{suffix}";
        var enrollments = $"valenrol{suffix}";

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = classes, DisplayName = classes,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "InstructorUser", DisplayName = "Instructor", Type = "string" },
                new FieldDefinition
                {
                    Name = "Salary", DisplayName = "Salary", Type = "decimal", Sensitivity = SensitivityLevel.Sensitive,
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
        return (classes, enrollments);
    }

    private static object Operators(string op = "_eq", object? expected = null) =>
        new Dictionary<string, object> { [op] = expected ?? "$CURRENT_USER" };

    /// <summary>A role body whose one permission holds the condition on the rule named.</summary>
    private static object Body(string type, string key, object operators, string rule = "read")
    {
        var conditional = new { enabled = true, conditions = new Dictionary<string, object> { [key] = operators } };
        var plain = new { enabled = false, conditions = new Dictionary<string, object>() };

        return new
        {
            name = $"Validated_{Guid.NewGuid():n}",
            description = "a role under test",
            permissions = new[]
            {
                new
                {
                    contentTypeSlug = type,
                    create = rule == "create" ? conditional : plain,
                    read = rule == "read" ? conditional : plain,
                    update = rule == "update" ? conditional : plain,
                    delete = rule == "delete" ? conditional : plain,
                    transitions = rule == "transition"
                        ? new Dictionary<string, object> { ["Confirm"] = conditional }
                        : new Dictionary<string, object>(),
                },
            },
            systemCapabilities = Array.Empty<string>(),
        };
    }

    private static async Task<Guid> ExistingRoleAsync(HttpClient admin)
    {
        var res = await admin.PostAsJsonAsync("/api/roles", new
        {
            name = $"Existing_{Guid.NewGuid():n}",
            description = "saved before the change under test",
            permissions = Array.Empty<object>(),
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>Sends the body as a create and as an update, and returns both answers.</summary>
    private static async Task<List<(string Route, HttpStatusCode Status, string Text)>> SaveAsync(
        HttpClient admin, Func<object> body)
    {
        var id = await ExistingRoleAsync(admin);

        var created = await admin.PostAsJsonAsync("/api/roles", body());
        var updated = await admin.PutAsJsonAsync($"/api/roles/{id}", body());

        return
        [
            ("POST", created.StatusCode, Words(await created.Content.ReadAsStringAsync())),
            ("PUT", updated.StatusCode, Words(await updated.Content.ReadAsStringAsync())),
        ];
    }

    /// <summary>
    /// Every name and every text in a JSON body, decoded. The raw body escapes an apostrophe and an
    /// angle bracket, so a search of it would miss a message and miss an echoed key alike.
    /// </summary>
    private static string Words(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var words = new List<string>();
            Collect(doc.RootElement, words);
            return string.Join("\n", words);
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private static void Collect(JsonElement element, List<string> words)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    words.Add(property.Name);
                    Collect(property.Value, words);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Collect(item, words);
                break;

            case JsonValueKind.String:
                words.Add(element.GetString() ?? string.Empty);
                break;
        }
    }

    private static void ShouldBeRefused(
        List<(string Route, HttpStatusCode Status, string Text)> answers, string saying)
    {
        answers.Should().HaveCount(2);

        foreach (var (route, status, text) in answers)
        {
            status.Should().Be(HttpStatusCode.BadRequest, "{0} answered {1}", route, text);
            text.Should().Contain(saying, "{0} says what is wrong", route);
        }
    }

    [Fact]
    public async Task A_condition_that_follows_a_declared_reference_is_saved()
    {
        var admin = await AdminAsync();
        var (_, enrollments) = await TypesAsync();

        foreach (var rule in new[] { "read", "update", "delete", "transition" })
        {
            var answers = await SaveAsync(admin, () => Body(enrollments, "Class.InstructorUser", Operators(), rule));

            answers.Should().HaveCount(2);
            foreach (var (route, status, text) in answers)
                status.Should().Be(HttpStatusCode.OK, "{0} of a {1} rule answered {2}", route, rule, text);
        }
    }

    [Fact]
    public async Task A_saved_condition_reads_back_as_it_was_sent()
    {
        var admin = await AdminAsync();
        var (_, enrollments) = await TypesAsync();

        var created = await admin.PostAsJsonAsync(
            "/api/roles", Body(enrollments, "Class.InstructorUser", Operators()));
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());

        using var saved = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = saved.RootElement.GetProperty("id").GetGuid();

        var read = await admin.GetAsync($"/api/roles/{id}");
        read.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        var permissions = doc.RootElement.GetProperty("permissions");
        permissions.GetArrayLength().Should().Be(1);

        permissions[0].GetProperty("read").GetProperty("conditions")
            .GetProperty("Class.InstructorUser").GetProperty("_eq").GetString()
            .Should().Be("$CURRENT_USER");
    }

    [Fact]
    public async Task A_condition_of_two_hops_is_refused()
    {
        var admin = await AdminAsync();
        var (_, enrollments) = await TypesAsync();

        ShouldBeRefused(
            await SaveAsync(admin, () => Body(enrollments, "Class.Teacher.InstructorUser", Operators())),
            "one reference is followed, not two");
    }

    [Theory]
    [InlineData("Class.")]
    [InlineData(".InstructorUser")]
    [InlineData("Class.$createdBy")]
    [InlineData("Class.<script>alert(1)</script>")]
    [InlineData("Class.Instructor User")]
    public async Task A_malformed_path_is_refused_and_not_repeated_back(string key)
    {
        var admin = await AdminAsync();
        var (_, enrollments) = await TypesAsync();

        var answers = await SaveAsync(admin, () => Body(enrollments, key, Operators()));

        ShouldBeRefused(answers, "written Reference.Field");
        foreach (var (route, _, text) in answers)
            text.Should().NotContain(key, "{0} must not echo a key that failed the check", route);
    }

    [Fact]
    public async Task A_condition_through_a_field_that_is_not_a_reference_is_refused()
    {
        var admin = await AdminAsync();
        var (_, enrollments) = await TypesAsync();

        ShouldBeRefused(
            await SaveAsync(admin, () => Body(enrollments, "Student.InstructorUser", Operators())),
            "'Student' is not a reference field");

        // A field the type does not declare gets the same words, which do not say which it was.
        ShouldBeRefused(
            await SaveAsync(admin, () => Body(enrollments, "Missing.InstructorUser", Operators())),
            "'Missing' is not a reference field");

        // A data key is matched in its own case, so this is not the field.
        ShouldBeRefused(
            await SaveAsync(admin, () => Body(enrollments, "class.InstructorUser", Operators())),
            "'class' is not a reference field");
    }

    [Fact]
    public async Task A_condition_on_a_field_the_referenced_type_does_not_declare_as_public_is_refused()
    {
        var admin = await AdminAsync();
        var (_, enrollments) = await TypesAsync();

        ShouldBeRefused(
            await SaveAsync(admin, () => Body(enrollments, "Class.Missing", Operators())),
            "'Missing' is not a Public field");

        ShouldBeRefused(
            await SaveAsync(admin, () => Body(enrollments, "Class.Salary", Operators())),
            "'Salary' is not a Public field");
    }

    [Fact]
    public async Task A_condition_on_a_content_type_this_tenant_does_not_define_is_refused()
    {
        var admin = await AdminAsync();
        var unknown = $"nosuchtype{Guid.NewGuid():n}"[..24];

        var answers = await SaveAsync(admin, () => Body(unknown, "Class.InstructorUser", Operators()));

        ShouldBeRefused(answers, "a content type this tenant does not define");
        foreach (var (route, _, text) in answers)
            text.Should().NotContain(unknown, "{0} must not echo the slug the request sent", route);
    }

    [Fact]
    public async Task A_condition_on_a_create_rule_is_refused()
    {
        var admin = await AdminAsync();
        var (_, enrollments) = await TypesAsync();

        ShouldBeRefused(
            await SaveAsync(admin, () => Body(enrollments, "Class.InstructorUser", Operators(), rule: "create")),
            "is on a Create rule");
    }

    [Fact]
    public async Task A_condition_with_no_operator_or_one_the_api_does_not_know_is_refused()
    {
        var admin = await AdminAsync();
        var (_, enrollments) = await TypesAsync();

        foreach (var operators in new object[]
                 {
                     new Dictionary<string, object>(),
                     Operators("_gt"),
                     Operators("eq"),
                     "$CURRENT_USER",
                     new[] { "$CURRENT_USER" },
                 })
        {
            ShouldBeRefused(
                await SaveAsync(admin, () => Body(enrollments, "Class.InstructorUser", operators)),
                "needs at least one operator");
        }
    }

    [Fact]
    public async Task A_refused_role_is_not_saved()
    {
        var admin = await AdminAsync();
        var (_, enrollments) = await TypesAsync();
        var id = await ExistingRoleAsync(admin);

        var refused = await admin.PutAsJsonAsync(
            $"/api/roles/{id}", Body(enrollments, "Student.InstructorUser", Operators()));
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var stored = await session.LoadAsync<Role>(id);

        stored.Should().NotBeNull();
        stored!.Permissions.Should().BeEmpty("the update was refused before anything was stored");
        stored.Name.Should().StartWith("Existing_");
    }

    [Fact]
    public async Task A_condition_on_one_field_is_saved_as_before_whatever_the_type()
    {
        var admin = await AdminAsync();
        var unknown = $"nosuchtype{Guid.NewGuid():n}"[..24];

        // No dot, so nothing here is looked up: not the type, not the field, not the operator.
        foreach (var body in new Func<object>[]
                 {
                     () => Body(unknown, "author", Operators()),
                     () => Body(unknown, "$createdBy", Operators()),
                     () => Body(unknown, "Anything", Operators("_gt", 5)),
                     () => Body(unknown, "author", Operators(), rule: "create"),
                 })
        {
            var answers = await SaveAsync(admin, body);

            answers.Should().HaveCount(2);
            foreach (var (route, status, text) in answers)
                status.Should().Be(HttpStatusCode.OK, "{0} answered {1}", route, text);
        }
    }

    [Fact]
    public async Task A_role_stored_with_a_condition_the_api_now_refuses_still_reads_back()
    {
        var admin = await AdminAsync();
        var id = Guid.NewGuid();
        var name = $"Legacy_{Guid.NewGuid():n}";

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Role
            {
                Id = id,
                Name = name,
                Permissions =
                [
                    new ContentTypePermission
                    {
                        ContentTypeSlug = "legacy-type",
                        Read = new PermissionRule
                        {
                            Enabled = true,
                            Conditions = new Dictionary<string, object>
                            {
                                ["a.b.c"] = new Dictionary<string, object> { ["_eq"] = "x" },
                            },
                        },
                    },
                ],
            });
            await session.SaveChangesAsync();
        }

        var read = await admin.GetAsync($"/api/roles/{id}");
        read.StatusCode.Should().Be(HttpStatusCode.OK, await read.Content.ReadAsStringAsync());

        using var doc = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("name").GetString().Should().Be(name);
        doc.RootElement.GetProperty("permissions")[0].GetProperty("read").GetProperty("conditions")
            .GetProperty("a.b.c").GetProperty("_eq").GetString().Should().Be("x");
    }
}

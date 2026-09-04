using System.Net;
using System.Net.Http.Headers;
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
/// Content type blueprints (issue #109): a site starts from a named set of types rather than an
/// empty schema, and applying one never replaces a type that is already there.
/// </summary>
/// <remarks>
/// Every apply runs in a tenant made for the test. The built-in names (post, author, page, event
/// and so on) are exactly the names other tests are likely to create in the default tenant, so
/// applying there would make this class and its neighbours fail on each other's leftovers.
/// </remarks>
[Collection("Sequential")]
public class ContentTypeBlueprintTests
{
    private readonly IntegrationTestFixture _factory;

    public ContentTypeBlueprintTests(IntegrationTestFixture factory) => _factory = factory;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string Blueprints = "/api/content-types/blueprints";

    private async Task<string> TenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"bp-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
        await session.SaveChangesAsync();
        return slug;
    }

    /// <summary>A SuperAdmin who is a member of the tenant, talking to the host given.</summary>
    private async Task<HttpClient> AdminInAsync(string tenantSlug, WebApplicationFactory<Program>? host = null)
    {
        var userId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = $"bp-{Guid.NewGuid():n}"[..14],
                Email = $"bp-{Guid.NewGuid():n}@example.com",
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
            await session.SaveChangesAsync();
        }

        var client = (host ?? _factory).CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(
                roles: ["SuperAdmin"],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = tenantSlug }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenantSlug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"10.9.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}");
        return client;
    }

    private static Task<HttpResponseMessage> ApplyAsync(HttpClient client, string name) =>
        client.PostAsync($"{Blueprints}/{name}", null, TestContext.Current.CancellationToken);

    private static async Task<ListResponse> ListAsync(HttpClient client)
    {
        var response = await client.GetAsync(Blueprints, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<ListResponse>(Json, TestContext.Current.CancellationToken))!;
    }

    private static async Task<List<string>> TypeNamesAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/content-types?pageSize=100", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("name").GetString()!)
            .ToList();
    }

    private static async Task<JsonElement> TypeAsync(HttpClient client, string name)
    {
        var response = await client.GetAsync("/api/content-types?pageSize=100", TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("name").GetString() == name)
            .Clone();
    }

    private sealed record ListResponse(List<ListItem> Items, List<string> Problems);

    private sealed record ListItem(
        string Name, string Description, bool BuiltIn, string? Source, List<string> ContentTypes, List<string> Errors);

    private static string TempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"barako-blueprints-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private const string AgencyBlueprint = """
        {
          "name": "agency",
          "description": "Case studies and the people who wrote them.",
          "contentTypes": [
            {
              "name": "case-study",
              "displayName": "Case study",
              "isPubliclyDeliverable": true,
              "fields": [
                { "name": "Title", "displayName": "Title", "type": "string", "isRequired": true },
                { "name": "Slug", "displayName": "Slug", "type": "slug", "isRequired": true },
                { "name": "Body", "displayName": "Body", "type": "richtext" },
                { "name": "Lead", "displayName": "Lead", "type": "reference", "referenceType": "consultant" }
              ]
            },
            {
              "name": "consultant",
              "displayName": "Consultant",
              "fields": [
                { "name": "Name", "displayName": "Name", "type": "string", "isRequired": true },
                { "name": "Rate", "displayName": "Day rate", "type": "money", "sensitivity": "Hidden" }
              ]
            }
          ]
        }
        """;

    // ---- the built-ins ----------------------------------------------------------------------

    [Fact]
    public async Task The_list_shows_the_four_built_in_blueprints_and_what_each_creates()
    {
        var client = await AdminInAsync(await TenantAsync());

        var list = await ListAsync(client);

        var builtIn = list.Items.Where(i => i.BuiltIn).ToList();
        builtIn.Select(i => i.Name).Should().BeEquivalentTo(["blog", "docs", "events", "portfolio"]);
        builtIn.Should().OnlyContain(i => i.Errors.Count == 0,
            "a shipped blueprint that fails its own validation is a bug, and this is where it shows");
        builtIn.Should().OnlyContain(i => i.Description.Length > 0);
        builtIn.Should().OnlyContain(i => i.Source == null);
        list.Problems.Should().BeEmpty("no custom directory is configured on this host");

        builtIn.Single(i => i.Name == "blog").ContentTypes.Should().Equal("post", "category", "author", "page");
        builtIn.Single(i => i.Name == "events").ContentTypes.Should().Equal("event", "venue", "speaker");
        builtIn.Single(i => i.Name == "portfolio").ContentTypes.Should().Equal("project", "client");
        builtIn.Single(i => i.Name == "docs").ContentTypes.Should().Equal("article", "section");
    }

    [Fact]
    public async Task Applying_blog_creates_its_types_and_applying_it_again_is_refused()
    {
        var client = await AdminInAsync(await TenantAsync());

        var first = await ApplyAsync(client, "blog");

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = JsonDocument.Parse(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)))
        {
            doc.RootElement.GetProperty("blueprint").GetString().Should().Be("blog");
            var created = doc.RootElement.GetProperty("created").EnumerateArray().ToList();
            created.Should().HaveCount(4);
            created.Select(c => c.GetProperty("name").GetString()).Should().Equal("post", "category", "author", "page");
            created.Should().OnlyContain(c => c.GetProperty("id").GetGuid() != Guid.Empty);
        }

        var names = await TypeNamesAsync(client);
        names.Should().Contain(["post", "category", "author", "page"]);

        var post = await TypeAsync(client, "post");
        post.GetProperty("isPubliclyDeliverable").GetBoolean().Should().BeTrue();
        post.GetProperty("eventSourced").GetBoolean().Should().BeFalse("a blueprint type is document sourced");
        var fields = post.GetProperty("fields").EnumerateArray().ToList();
        fields.Should().NotBeEmpty();
        fields.Should().Contain(f => f.GetProperty("name").GetString() == "Slug" && f.GetProperty("type").GetString() == "slug");
        fields.Single(f => f.GetProperty("name").GetString() == "Author")
            .GetProperty("referenceType").GetString().Should().Be("author");

        var author = await TypeAsync(client, "author");
        author.GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == "Email")
            .GetProperty("sensitivity").GetString().Should().Be("Sensitive",
                "an author's email is for the editorial team, and the blueprint has to say so");

        var second = await ApplyAsync(client, "blog");

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("post").And.Contain("author", "the refusal names every type that clashes");

        (await TypeNamesAsync(client)).Count(n => n == "post").Should().Be(1);
    }

    [Fact]
    public async Task Applying_on_a_tenant_that_already_has_an_unrelated_type_succeeds()
    {
        var client = await AdminInAsync(await TenantAsync());

        var newsletter = await client.PostAsJsonAsync("/api/content-types", new
        {
            name = "newsletter",
            displayName = "Newsletter",
            fields = new[] { new { name = "Subject", displayName = "Subject", type = "string" } },
        }, TestContext.Current.CancellationToken);
        newsletter.StatusCode.Should().Be(HttpStatusCode.OK);

        var applied = await ApplyAsync(client, "portfolio");

        applied.StatusCode.Should().Be(HttpStatusCode.OK);
        var names = await TypeNamesAsync(client);
        names.Should().Contain(["newsletter", "project", "client"]);
    }

    [Fact]
    public async Task One_clashing_type_refuses_the_whole_blueprint()
    {
        var client = await AdminInAsync(await TenantAsync());

        var venue = await client.PostAsJsonAsync("/api/content-types", new
        {
            name = "venue",
            displayName = "Venue",
            fields = new[] { new { name = "Name", displayName = "Name", type = "string" } },
        }, TestContext.Current.CancellationToken);
        venue.StatusCode.Should().Be(HttpStatusCode.OK);

        var applied = await ApplyAsync(client, "events");

        applied.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await applied.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("venue");
        var names = await TypeNamesAsync(client);
        names.Should().Contain("venue");
        names.Should().NotContain("event", "a partial apply leaves references pointing at a type the blueprint did not shape");
        names.Should().NotContain("speaker");
    }

    [Fact]
    public async Task Applying_a_blueprint_that_does_not_exist_is_a_404()
    {
        var client = await AdminInAsync(await TenantAsync());

        var response = await ApplyAsync(client, "no-such-blueprint");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Blueprints_are_tenant_scoped()
    {
        var first = await AdminInAsync(await TenantAsync());
        var second = await AdminInAsync(await TenantAsync());

        (await ApplyAsync(first, "docs")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ApplyAsync(second, "docs")).StatusCode.Should().Be(HttpStatusCode.OK,
            "the types the first tenant got are not a clash for the second");
        (await TypeNamesAsync(second)).Should().Contain(["article", "section"]);
    }

    [Fact]
    public async Task Applying_a_blueprint_refreshes_the_delivery_document_and_is_audited()
    {
        var tenant = await TenantAsync();
        var client = await AdminInAsync(tenant);

        // Served once first, so a missing invalidation would hand back this stale document.
        using (var before = await DeliveryDocumentAsync(tenant))
        {
            before.RootElement.GetProperty("paths").TryGetProperty("/api/public/post", out _).Should().BeFalse();
        }

        (await ApplyAsync(client, "blog")).StatusCode.Should().Be(HttpStatusCode.OK);

        using var after = await DeliveryDocumentAsync(tenant);
        after.RootElement.GetProperty("paths").TryGetProperty("/api/public/post", out _).Should().BeTrue(
            "a new deliverable type has to show up in the delivery document without a restart");

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var audit = await session.Query<AuditEvent>()
            .Where(e => e.Action == "contenttype.blueprint_applied" && e.TenantSlug == tenant)
            .ToListAsync(TestContext.Current.CancellationToken);
        audit.Should().ContainSingle().Which.TargetId.Should().Be("blog");
        audit[0].Metadata!["created"].ToString().Should().Contain("post");
    }

    private async Task<JsonDocument> DeliveryDocumentAsync(string tenantSlug)
    {
        var anonymous = _factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Tenant", tenantSlug);
        var response = await anonymous.GetAsync("/swagger/v1/swagger.json", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    // ---- custom blueprints -----------------------------------------------------------------

    [Fact]
    public async Task A_custom_blueprint_in_the_configured_directory_is_listed_and_applies()
    {
        var dir = TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(dir, "agency.json"), AgencyBlueprint, TestContext.Current.CancellationToken);
        var host = _factory.WithSetting("Blueprints:Path", dir);
        var client = await AdminInAsync(await TenantAsync(), host);

        var list = await ListAsync(client);

        list.Problems.Should().BeEmpty();
        list.Items.Where(i => i.BuiltIn).Should().HaveCount(4, "a custom directory adds to the built-ins");
        var agency = list.Items.Single(i => i.Name == "agency");
        agency.BuiltIn.Should().BeFalse();
        agency.Source.Should().Be("agency.json");
        agency.Errors.Should().BeEmpty();
        agency.ContentTypes.Should().Equal("case-study", "consultant");

        var applied = await ApplyAsync(client, "agency");

        applied.StatusCode.Should().Be(HttpStatusCode.OK);
        (await TypeNamesAsync(client)).Should().Contain(["case-study", "consultant"]);
        var consultant = await TypeAsync(client, "consultant");
        consultant.GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == "Rate")
            .GetProperty("sensitivity").GetString().Should().Be("Hidden");
    }

    [Fact]
    public async Task An_invalid_custom_file_is_reported_by_the_list_and_refused_by_the_apply()
    {
        var dir = TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(dir, "broken.json"), """
            {
              "name": "broken",
              "contentTypes": [
                {
                  "name": "thing",
                  "displayName": "Thing",
                  "fields": [
                    { "name": "Title", "displayName": "Title", "type": "wibble" },
                    { "name": "Owner", "displayName": "Owner", "type": "reference", "referenceType": "person" }
                  ]
                }
              ]
            }
            """, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dir, "notjson.json"), "{ this is not", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dir, "typo.json"), """
            {
              "name": "typo",
              "contentTypes": [
                {
                  "name": "note",
                  "displayName": "Note",
                  "fields": [ { "name": "Secret", "displayName": "Secret", "type": "string", "sensitivty": "Hidden" } ]
                }
              ]
            }
            """, TestContext.Current.CancellationToken);
        var host = _factory.WithSetting("Blueprints:Path", dir);
        var client = await AdminInAsync(await TenantAsync(), host);

        var list = await ListAsync(client);

        var broken = list.Items.Single(i => i.Name == "broken");
        broken.Errors.Should().HaveCount(2);
        broken.Errors.Should().Contain(e => e.Contains("wibble"));
        broken.Errors.Should().Contain(e => e.Contains("person"), "a reference must point inside the blueprint");

        var notJson = list.Items.Single(i => i.Name == "notjson");
        notJson.Source.Should().Be("notjson.json");
        notJson.Errors.Should().ContainSingle().Which.Should().Contain("could not be read");

        var typo = list.Items.Single(i => i.Name == "typo");
        typo.Errors.Should().ContainSingle().Which.Should().Contain("sensitivty",
            "a misspelt property would otherwise leave a field Public that the author marked Hidden");

        var applied = await ApplyAsync(client, "broken");

        applied.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await applied.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("wibble");
        (await ApplyAsync(client, "notjson")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TypeNamesAsync(client)).Should().NotContain("thing");
    }

    [Fact]
    public async Task A_custom_file_cannot_shadow_a_built_in_blueprint()
    {
        var dir = TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(dir, "blog.json"), """
            {
              "name": "blog",
              "contentTypes": [
                { "name": "post", "displayName": "Post", "fields": [ { "name": "Title", "displayName": "Title", "type": "string" } ] }
              ]
            }
            """, TestContext.Current.CancellationToken);
        var host = _factory.WithSetting("Blueprints:Path", dir);
        var client = await AdminInAsync(await TenantAsync(), host);

        var list = await ListAsync(client);

        var entries = list.Items.Where(i => i.Name == "blog").ToList();
        entries.Should().HaveCount(2);
        entries.Single(i => i.BuiltIn).Errors.Should().BeEmpty();
        entries.Single(i => !i.BuiltIn).Errors.Should().ContainSingle().Which.Should().Contain("built-in");

        var applied = await ApplyAsync(client, "blog");

        applied.StatusCode.Should().Be(HttpStatusCode.OK);
        (await TypeNamesAsync(client)).Should().Contain(["post", "category", "author", "page"],
            "the built-in is what applies, not the file that tried to take its name");
    }

    [Fact]
    public async Task A_configured_directory_that_does_not_exist_is_a_listed_problem_not_a_failure()
    {
        var host = _factory.WithSetting("Blueprints:Path", Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}"));
        var client = await AdminInAsync(await TenantAsync(), host);

        var list = await ListAsync(client);

        list.Problems.Should().ContainSingle().Which.Should().Contain("Blueprints:Path");
        list.Items.Where(i => i.BuiltIn).Should().HaveCount(4);
    }

    [Fact]
    public async Task An_oversized_custom_file_is_reported_invalid_and_never_parsed()
    {
        var dir = TempDirectory();
        var huge = new
        {
            name = "huge",
            description = new string('x', 300 * 1024),
            contentTypes = new[]
            {
                new
                {
                    name = "thing",
                    displayName = "Thing",
                    fields = new[] { new { name = "Title", displayName = "Title", type = "string" } },
                },
            },
        };
        await File.WriteAllTextAsync(
            Path.Combine(dir, "huge.json"), JsonSerializer.Serialize(huge), TestContext.Current.CancellationToken);
        var host = _factory.WithSetting("Blueprints:Path", dir);
        var client = await AdminInAsync(await TenantAsync(), host);

        var list = await ListAsync(client);

        var entry = list.Items.Single(i => i.Name == "huge");
        entry.Source.Should().Be("huge.json");
        entry.Errors.Should().ContainSingle().Which.Should().Contain("larger than 256 KB");

        var applied = await ApplyAsync(client, "huge");

        applied.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.NotFound);
        (await TypeNamesAsync(client)).Should().NotContain("thing");
    }

    [Fact]
    public async Task An_unreadable_custom_file_names_only_the_file_not_the_server_path()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("chmod does not restrict read access on Windows");
        }

        var dir = TempDirectory();
        var path = Path.Combine(dir, "secret.json");
        await File.WriteAllTextAsync(path, AgencyBlueprint, TestContext.Current.CancellationToken);
        File.SetUnixFileMode(path, UnixFileMode.None);

        try
        {
            var host = _factory.WithSetting("Blueprints:Path", dir);
            var client = await AdminInAsync(await TenantAsync(), host);

            var list = await ListAsync(client);

            var entry = list.Items.Single(i => i.Name == "secret");
            entry.Errors.Should().ContainSingle();
            entry.Errors[0].Should().Contain("secret.json");
            entry.Errors[0].Should().NotContain(dir);

            var applied = await ApplyAsync(client, "secret");
            var body = await applied.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            applied.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            body.Should().Contain("secret.json");
            body.Should().NotContain(dir);
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}

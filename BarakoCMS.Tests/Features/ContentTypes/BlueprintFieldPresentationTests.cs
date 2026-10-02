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
/// A blueprint file may carry a field's editor hint, section and role and a type's route template.
/// Applying it stores them, and a file that declares one the content type endpoints would refuse
/// is listed with the reason and not applied.
/// </summary>
/// <remarks>
/// One directory and one derived host for the class, and a tenant per test, so an apply here
/// cannot meet a type another class created. The directory is written before each test and
/// removed after it.
/// </remarks>
[Collection("Sequential")]
public class BlueprintFieldPresentationTests : IDisposable
{
    private const string Blueprints = "/api/content-types/blueprints";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly string BlueprintDirectory =
        Path.Combine(Path.GetTempPath(), $"barako-blueprints-{Guid.NewGuid():N}");

    private static readonly Lock Gate = new();
    private static WebApplicationFactory<Program>? _host;

    private readonly IntegrationTestFixture _factory;

    public BlueprintFieldPresentationTests(IntegrationTestFixture factory)
    {
        _factory = factory;

        Directory.CreateDirectory(BlueprintDirectory);
        File.WriteAllText(Path.Combine(BlueprintDirectory, "hinted.json"), Hinted);
        File.WriteAllText(Path.Combine(BlueprintDirectory, "badhint.json"), BadHint);
        File.WriteAllText(Path.Combine(BlueprintDirectory, "badroute.json"), BadRoute);
    }

    public void Dispose()
    {
        if (Directory.Exists(BlueprintDirectory))
            Directory.Delete(BlueprintDirectory, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Hinted = """
        {
          "name": "hinted",
          "description": "A type whose fields say how they are edited.",
          "contentTypes": [
            {
              "name": "school-event",
              "displayName": "School event",
              "routeTemplate": "/whats-on/{slug}",
              "fields": [
                { "name": "EventName", "displayName": "Event name", "type": "string", "role": "title", "section": "Details" },
                { "name": "Slug", "displayName": "Slug", "type": "slug", "section": "Details" },
                { "name": "Sections", "displayName": "Sections", "type": "json", "editor": "blocks", "section": "Page" },
                { "name": "Notes", "displayName": "Notes", "type": "text" }
              ]
            }
          ]
        }
        """;

    private const string BadHint = """
        {
          "name": "badhint",
          "contentTypes": [
            {
              "name": "hint-thing",
              "displayName": "Thing",
              "fields": [ { "name": "Sections", "displayName": "Sections", "type": "json", "editor": "kanban-board" } ]
            }
          ]
        }
        """;

    private const string BadRoute = """
        {
          "name": "badroute",
          "contentTypes": [
            {
              "name": "route-thing",
              "displayName": "Thing",
              "routeTemplate": "@other.example/{slug}",
              "fields": [ { "name": "Title", "displayName": "Title", "type": "string" } ]
            }
          ]
        }
        """;

    // Never disposed: a derived host shares the fixture's server.
    private WebApplicationFactory<Program> BlueprintHost()
    {
        lock (Gate)
        {
            return _host ??= _factory.WithSetting("Blueprints:Path", BlueprintDirectory);
        }
    }

    private async Task<string> TenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"bh-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    private async Task<HttpClient> AdminInAsync(string tenantSlug)
    {
        var userId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = $"bh-{Guid.NewGuid():n}"[..14],
                Email = $"bh-{Guid.NewGuid():n}@example.com",
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

        var client = BlueprintHost().CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(
                roles: ["SuperAdmin"],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = tenantSlug }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenantSlug);
        client.DefaultRequestHeaders.Add(
            TestRemoteIpFilter.Header, $"10.8.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}");
        return client;
    }

    private sealed record ListResponse(List<ListItem> Items, List<string> Problems);

    private sealed record ListItem(
        string Name, string Description, bool BuiltIn, string? Source, List<string> ContentTypes, List<string> Errors);

    private static async Task<ListResponse> ListAsync(HttpClient client)
    {
        var response = await client.GetAsync(Blueprints, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<ListResponse>(Json, Ct))!;
    }

    private async Task<ContentTypeDefinition?> StoredAsync(string tenantSlug, string type)
    {
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(tenantSlug);
        return await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type, Ct);
    }

    [Fact]
    public async Task A_blueprint_carrying_hints_roles_and_a_route_template_applies_with_them()
    {
        var tenant = await TenantAsync();
        var client = await AdminInAsync(tenant);

        var hinted = (await ListAsync(client)).Items.Single(i => i.Name == "hinted");
        hinted.Errors.Should().BeEmpty();

        var applied = await client.PostAsync($"{Blueprints}/hinted", null, Ct);
        applied.StatusCode.Should().Be(HttpStatusCode.OK, await applied.Content.ReadAsStringAsync(Ct));

        var stored = await StoredAsync(tenant, "school-event");
        stored.Should().NotBeNull();
        stored!.RouteTemplate.Should().Be("/whats-on/{slug}");
        stored.Fields.Should().HaveCount(4);

        var name = stored.Fields.Single(f => f.Name == "EventName");
        name.Role.Should().Be("title");
        name.Section.Should().Be("Details");

        var sections = stored.Fields.Single(f => f.Name == "Sections");
        sections.Editor.Should().Be("blocks");
        sections.Section.Should().Be("Page");

        var notes = stored.Fields.Single(f => f.Name == "Notes");
        notes.Editor.Should().BeNull();
        notes.Section.Should().BeNull();
        notes.Role.Should().BeNull();
    }

    [Fact]
    public async Task A_blueprint_with_an_unknown_editor_or_a_bad_route_template_is_listed_with_the_reason_and_not_applied()
    {
        var tenant = await TenantAsync();
        var client = await AdminInAsync(tenant);

        var list = await ListAsync(client);

        var badHint = list.Items.Single(i => i.Name == "badhint");
        badHint.Errors.Should().ContainSingle().Which.Should().Contain("Sections").And.Contain("Accepted values");
        badHint.Errors[0].Should().NotContain("kanban-board");

        var badRoute = list.Items.Single(i => i.Name == "badroute");
        badRoute.Errors.Should().ContainSingle().Which.Should().Contain("route-thing").And.Contain("routeTemplate");

        (await client.PostAsync($"{Blueprints}/badhint", null, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsync($"{Blueprints}/badroute", null, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await StoredAsync(tenant, "hint-thing")).Should().BeNull();
        (await StoredAsync(tenant, "route-thing")).Should().BeNull();
    }

    /// <summary>
    /// The shipped blueprints declare none of the new members, so the readers treat their types as
    /// they did. Pinned, so adding a role to one is a decision made here and in the feed tests.
    /// </summary>
    [Fact]
    public async Task The_built_in_blueprints_still_validate_and_declare_no_hint_role_or_route_template()
    {
        var builtIn = (await ListAsync(await AdminInAsync(await TenantAsync()))).Items.Where(i => i.BuiltIn).ToList();

        builtIn.Should().HaveCount(6);
        builtIn.Should().OnlyContain(i => i.Errors.Count == 0);

        using var scope = _factory.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<barakoCMS.Features.ContentType.Blueprints.BlueprintCatalog>();

        foreach (var item in builtIn)
        {
            var types = catalog.Find(item.Name)!.Definition!.ContentTypes;
            types.Should().NotBeEmpty();
            types.Should().OnlyContain(t => t.RouteTemplate == null);

            var fields = types.SelectMany(t => t.Fields).ToList();
            fields.Should().NotBeEmpty();
            fields.Should().OnlyContain(f => f.Editor == null && f.Section == null && f.Role == null);
        }
    }
}

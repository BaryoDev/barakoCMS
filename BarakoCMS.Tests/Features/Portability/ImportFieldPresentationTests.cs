using System.Net;
using System.Net.Http.Json;
using BarakoCMS.Portability;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Portability;

/// <summary>
/// A field's editor hint, section and role and a type's route template travel in a bundle, and an
/// import holds them to the rules the content type endpoints apply.
/// </summary>
[Collection("Sequential")]
public class ImportFieldPresentationTests
{
    private static int _ipCounter;

    private readonly IntegrationTestFixture _fixture;

    public ImportFieldPresentationTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> TenantAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"hnt-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    private async Task<HttpClient> AdminOfAsync(string tenantSlug)
    {
        var userId = Guid.NewGuid();

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = $"hnt-{Guid.NewGuid():n}"[..14],
                Email = $"hnt-{Guid.NewGuid():n}@example.com",
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

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(
                roles: ["SuperAdmin", "Admin"],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = tenantSlug }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenantSlug);
        client.DefaultRequestHeaders.Add(
            TestRemoteIpFilter.Header, $"198.51.103.{Interlocked.Increment(ref _ipCounter) % 200 + 20}");
        return client;
    }

    private static string NewType() => $"event{Guid.NewGuid():n}"[..14];

    private static List<FieldDefinition> HintedFields() =>
    [
        new() { Name = "EventName", DisplayName = "Event name", Type = "string", Role = "title", Section = "Details" },
        new() { Name = "Sections", DisplayName = "Sections", Type = "json", Editor = "blocks", Section = "Page" },
        new() { Name = "Slug", DisplayName = "Slug", Type = "slug" },
    ];

    private async Task StoreTypeAsync(string tenantSlug, string type, string? routeTemplate, List<FieldDefinition> fields)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenantSlug);
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = type, DisplayName = "Event", RouteTemplate = routeTemplate, Fields = fields,
        });
        await session.SaveChangesAsync(Ct);
    }

    private async Task<ContentTypeDefinition?> StoredAsync(string tenantSlug, string type)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(tenantSlug);
        return await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type, Ct);
    }

    private static async Task<PortabilityBundle> ExportAsync(HttpClient client, string type)
    {
        var exported = await client.GetAsync($"/api/portability/export?types={type}", Ct);
        exported.StatusCode.Should().Be(HttpStatusCode.OK);

        var bundle = await exported.Content.ReadFromJsonAsync<PortabilityBundle>(ApiJson.Options, Ct);
        bundle!.ContentTypes.Should().HaveCount(1);
        return bundle;
    }

    private static Task<HttpResponseMessage> ImportAsync(HttpClient client, PortabilityBundle bundle) =>
        client.PostAsJsonAsync(
            "/api/portability/import",
            new { contentTypes = bundle.ContentTypes, contents = bundle.Contents },
            ApiJson.Options,
            Ct);

    [Fact]
    public async Task A_type_exports_its_hints_roles_and_route_template_and_another_tenant_imports_them()
    {
        var source = await TenantAsync();
        var destination = await TenantAsync();
        var type = NewType();
        await StoreTypeAsync(source, type, "/whats-on/{slug}", HintedFields());

        var bundle = await ExportAsync(await AdminOfAsync(source), type);

        // They have to be in the bundle, or the import below proves nothing about them.
        bundle.ContentTypes[0].RouteTemplate.Should().Be("/whats-on/{slug}");
        bundle.ContentTypes[0].Fields.Should().HaveCount(3);
        bundle.ContentTypes[0].Fields.Single(f => f.Name == "Sections").Editor.Should().Be("blocks");
        bundle.ContentTypes[0].Fields.Single(f => f.Name == "EventName").Role.Should().Be("title");

        var imported = await ImportAsync(await AdminOfAsync(destination), bundle);

        imported.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", imported.StatusCode,
            await imported.Content.ReadAsStringAsync(Ct));

        var landed = await StoredAsync(destination, type);
        landed.Should().NotBeNull();
        landed!.RouteTemplate.Should().Be("/whats-on/{slug}");
        landed.Fields.Should().HaveCount(3);

        var sections = landed.Fields.Single(f => f.Name == "Sections");
        sections.Editor.Should().Be("blocks");
        sections.Section.Should().Be("Page");

        var name = landed.Fields.Single(f => f.Name == "EventName");
        name.Role.Should().Be("title");
        name.Section.Should().Be("Details");
    }

    [Fact]
    public async Task Importing_over_a_stored_type_takes_the_bundles_route_template_and_hints()
    {
        var tenant = await TenantAsync();
        var type = NewType();
        await StoreTypeAsync(tenant, type, "/old/{slug}",
        [
            new() { Name = "EventName", DisplayName = "Event name", Type = "string" },
            new() { Name = "Sections", DisplayName = "Sections", Type = "json" },
            new() { Name = "Slug", DisplayName = "Slug", Type = "slug" },
        ]);
        var admin = await AdminOfAsync(tenant);

        var bundle = await ExportAsync(admin, type);
        bundle.ContentTypes[0].RouteTemplate = "/new/{slug}";
        bundle.ContentTypes[0].Fields = HintedFields();

        var imported = await ImportAsync(admin, bundle);

        imported.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", imported.StatusCode,
            await imported.Content.ReadAsStringAsync(Ct));

        var stored = await StoredAsync(tenant, type);
        stored!.RouteTemplate.Should().Be("/new/{slug}");
        stored.Fields.Should().HaveCount(3);
        stored.Fields.Single(f => f.Name == "Sections").Editor.Should().Be("blocks");
        stored.Fields.Single(f => f.Name == "EventName").Role.Should().Be("title");
    }

    /// <summary>
    /// A bundle exported before these members existed carries none of them. Imported over a type
    /// that has since been given hints and a route template, it must leave them as stored.
    /// </summary>
    [Fact]
    public async Task A_bundle_that_carries_none_of_the_members_keeps_what_the_stored_type_holds()
    {
        var tenant = await TenantAsync();
        var type = NewType();
        await StoreTypeAsync(tenant, type, "/whats-on/{slug}", HintedFields());
        var admin = await AdminOfAsync(tenant);

        var bundle = await ExportAsync(admin, type);
        bundle.ContentTypes[0].RouteTemplate = null;
        bundle.ContentTypes[0].Fields.Should().HaveCount(3);
        foreach (var field in bundle.ContentTypes[0].Fields)
        {
            field.Editor = null;
            field.Section = null;
            field.Role = null;
        }

        // Something the bundle does change, so a 200 that applied nothing cannot pass.
        bundle.ContentTypes[0].Fields.Single(f => f.Name == "EventName").DisplayName = "Name of the event";

        var imported = await ImportAsync(admin, bundle);

        imported.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", imported.StatusCode,
            await imported.Content.ReadAsStringAsync(Ct));

        var stored = await StoredAsync(tenant, type);
        stored!.RouteTemplate.Should().Be("/whats-on/{slug}");
        stored.Fields.Should().HaveCount(3);

        var name = stored.Fields.Single(f => f.Name == "EventName");
        name.DisplayName.Should().Be("Name of the event");
        name.Role.Should().Be("title");
        name.Section.Should().Be("Details");

        var sections = stored.Fields.Single(f => f.Name == "Sections");
        sections.Editor.Should().Be("blocks");
        sections.Section.Should().Be("Page");
    }

    [Fact]
    public async Task A_bundle_can_move_a_role_to_another_field_and_a_changed_field_type_drops_the_stored_hint()
    {
        var tenant = await TenantAsync();
        var type = NewType();
        var fields = HintedFields();
        fields.Add(new FieldDefinition { Name = "Headline", DisplayName = "Headline", Type = "string" });
        await StoreTypeAsync(tenant, type, null, fields);
        var admin = await AdminOfAsync(tenant);

        var bundle = await ExportAsync(admin, type);
        bundle.ContentTypes[0].Fields.Should().HaveCount(4);
        bundle.ContentTypes[0].Fields.Single(f => f.Name == "EventName").Role = null;
        bundle.ContentTypes[0].Fields.Single(f => f.Name == "Headline").Role = "title";

        var blocks = bundle.ContentTypes[0].Fields.Single(f => f.Name == "Sections");
        blocks.Type = "text";
        blocks.Editor = null;

        var imported = await ImportAsync(admin, bundle);

        imported.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", imported.StatusCode,
            await imported.Content.ReadAsStringAsync(Ct));

        var stored = await StoredAsync(tenant, type);
        stored!.Fields.Should().HaveCount(4);
        stored.Fields.Single(f => f.Name == "Headline").Role.Should().Be("title");
        stored.Fields.Single(f => f.Name == "EventName").Role.Should().BeNull();

        var sections = stored.Fields.Single(f => f.Name == "Sections");
        sections.Type.Should().Be("text");
        sections.Editor.Should().BeNull("the blocks editor is not for a text field, so it is not carried over");
    }

    [Fact]
    public async Task A_bundle_with_an_unknown_editor_a_repeated_role_or_a_bad_route_template_is_refused_and_stores_nothing()
    {
        var source = await TenantAsync();
        var destination = await TenantAsync();
        var type = NewType();
        await StoreTypeAsync(source, type, "/whats-on/{slug}", HintedFields());

        var exporter = await AdminOfAsync(source);
        var importer = await AdminOfAsync(destination);

        var unknownEditor = await ExportAsync(exporter, type);
        unknownEditor.ContentTypes[0].Fields.Single(f => f.Name == "Sections").Editor = "kanban-board";
        var first = await ImportAsync(importer, unknownEditor);
        first.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var firstBody = await first.Content.ReadAsStringAsync(Ct);
        firstBody.Should().Contain("Sections").And.Contain("Accepted values");
        firstBody.Should().NotContain("kanban-board");

        var repeatedRole = await ExportAsync(exporter, type);
        repeatedRole.ContentTypes[0].Fields.Add(
            new FieldDefinition { Name = "Headline", DisplayName = "Headline", Type = "string", Role = "title" });
        var second = await ImportAsync(importer, repeatedRole);
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await second.Content.ReadAsStringAsync(Ct)).Should().Contain("EventName").And.Contain("Headline");

        var badRoute = await ExportAsync(exporter, type);
        badRoute.ContentTypes[0].RouteTemplate = "@other.example/{slug}";
        var third = await ImportAsync(importer, badRoute);
        third.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await third.Content.ReadAsStringAsync(Ct)).Should().Contain("routeTemplate");

        (await StoredAsync(destination, type)).Should().BeNull();
    }

    /// <summary>
    /// A type stored over the field cap is validated a cap's worth of fields at a time, so two
    /// fields declaring one role can sit in different chunks.
    /// </summary>
    [Fact]
    public async Task A_role_declared_twice_is_refused_when_the_two_fields_are_validated_in_different_chunks()
    {
        var tenant = await TenantAsync();
        var type = NewType();
        var count = barakoCMS.Infrastructure.Services.ContentTypeFieldLimit.Default + 2;

        List<FieldDefinition> Fields() => Enumerable.Range(0, count)
            .Select(i => new FieldDefinition { Name = $"F{i:000}", DisplayName = $"F{i:000}", Type = "string" })
            .ToList();

        await StoreTypeAsync(tenant, type, null, Fields());
        var admin = await AdminOfAsync(tenant);

        var bundle = await ExportAsync(admin, type);
        bundle.ContentTypes[0].Fields.Should().HaveCount(count);
        bundle.ContentTypes[0].Fields[0].Role = "title";

        var oneHolder = await ImportAsync(admin, bundle);
        oneHolder.IsSuccessStatusCode.Should().BeTrue("the control, one field holding the role imports: {0}",
            await oneHolder.Content.ReadAsStringAsync(Ct));

        bundle.ContentTypes[0].Fields[count - 1].Role = "title";
        var twoHolders = await ImportAsync(admin, bundle);

        twoHolders.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await twoHolders.Content.ReadAsStringAsync(Ct)).Should().Contain("role");

        var stored = await StoredAsync(tenant, type);
        stored!.Fields.Should().HaveCount(count);
        stored.Fields.Count(f => f.Role == "title").Should().Be(1);
    }
}

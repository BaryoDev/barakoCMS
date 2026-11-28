using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BarakoCMS.Tests.Features.Config;
using FluentAssertions;
using Marten;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Portability;

/// <summary>
/// Export, then apply what came out, and see whether the site on the other side is the same one.
/// </summary>
/// <remarks>
/// Moving a site runs during a migration, which is the moment when losing content hurts most and
/// when nobody is watching closely enough to notice a field that quietly did not survive the trip.
/// The round trip is the only assertion that covers both halves at once: a test of export alone
/// passes on a document no apply can read, and a test of apply alone passes on a document nothing
/// produces.
///
/// The tenant test is the one that would be a security bug rather than a data bug. A document
/// carries no tenant identity by design, so nothing in the file says where it belongs and the only
/// thing deciding is the tenant of the request carrying it.
/// </remarks>
[Collection("Integration1")]
public class RoundTripTests
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ConfigCallers _callers;

    public RoundTripTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _callers = new ConfigCallers(fixture);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<string> TenantAsync() => _callers.TenantAsync();

    private Task<HttpClient> AdminOfAsync(string tenantSlug) => _callers.SuperAdminAsync(tenantSlug);

    private static object Document(string type, string title) => new
    {
        version = 1,
        contentTypes = new[]
        {
            new
            {
                name = type,
                displayName = "Round Tripped",
                description = "a type that has to survive the trip",
                isPubliclyDeliverable = true,
                fields = new[]
                {
                    new { name = "Title", displayName = "Title", type = "string" },
                    new { name = "Body", displayName = "Body", type = "text" },
                },
            },
        },
        contents = new[]
        {
            new
            {
                contentType = type,
                status = "Published",
                data = new Dictionary<string, object> { ["Title"] = title, ["Body"] = "the body" },
            },
        },
    };

    /// <summary>The types and entries a tenant holds, as <c>GET /api/config/export</c> writes them.</summary>
    private static async Task<JsonElement> ExportAsync(HttpClient client)
    {
        var (exported, body) = await ConfigCallers.ExportAsync(client, "contentTypes,contents");
        exported.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        return body;
    }

    private static async Task ApplyOkAsync(HttpClient client, object document)
    {
        var (applied, body) = await ConfigCallers.ApplyAsync(client, document);
        applied.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
    }

    /// <summary>
    /// What comes out of export goes back in, and the type and its content arrive intact.
    /// </summary>
    [Fact]
    public async Task An_exported_document_applies_to_a_clean_tenant_with_its_schema_and_content()
    {
        var source = await TenantAsync();
        var destination = await TenantAsync();
        var type = $"trip{Guid.NewGuid():n}"[..12];
        var title = $"Kumquat {Guid.NewGuid():n}"[..16];

        var sourceAdmin = await AdminOfAsync(source);
        await ApplyOkAsync(sourceAdmin, Document(type, title));

        var exported = await ExportAsync(sourceAdmin);
        exported.GetProperty("contentTypes").EnumerateObject().Should().ContainSingle().Which.Name.Should().Be(type);
        exported.GetProperty("contents").EnumerateObject().Should().ContainSingle();

        var destinationAdmin = await AdminOfAsync(destination);
        await ApplyOkAsync(destinationAdmin, exported);

        var arrived = await ExportAsync(destinationAdmin);

        var definition = arrived.GetProperty("contentTypes").EnumerateObject().Should().ContainSingle().Subject;
        definition.Name.Should().Be(type);
        definition.Value.GetProperty("displayName").GetString().Should().Be("Round Tripped");
        definition.Value.GetProperty("isPubliclyDeliverable").GetBoolean().Should().BeTrue("the delivery flag is part of the schema");
        definition.Value.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString())
            .Should().BeEquivalentTo(["Title", "Body"],
                "a field lost in transit is content that stops being editable on the other side");

        var record = arrived.GetProperty("contents").EnumerateObject().Should().ContainSingle().Subject.Value;
        record.GetProperty("contentType").GetString().Should().Be(type);
        record.GetProperty("data").GetProperty("Title").GetString().Should().Be(title);
        record.GetProperty("data").GetProperty("Body").GetString().Should().Be("the body");
    }

    /// <summary>
    /// The singleton flag survives the trip, and an apply of entries is deliberately not capped by it.
    /// </summary>
    /// <remarks>
    /// Two claims in one test because they are the same decision. The flag has to round trip, or a
    /// migration turns a site's settings type back into a list. The cap is not applied here on
    /// purpose: it runs during a restore, where dropping rows the document holds loses content at
    /// the worst possible moment. So a document with two entries of a singleton type lands both, and
    /// this test is what says so out loud.
    /// </remarks>
    [Fact]
    public async Task A_singleton_type_round_trips_and_an_apply_of_entries_is_not_capped()
    {
        var tenant = await TenantAsync();
        var admin = await AdminOfAsync(tenant);
        var type = $"single{Guid.NewGuid():n}"[..12];

        var document = new
        {
            version = 1,
            contentTypes = new[]
            {
                new
                {
                    name = type,
                    displayName = "Site Settings",
                    isSingleton = true,
                    fields = new[] { new { name = "Phone", displayName = "Phone", type = "string" } },
                },
            },
            contents = new[]
            {
                new
                {
                    contentType = type,
                    status = "Published",
                    data = new Dictionary<string, object> { ["Phone"] = "first" },
                },
                new
                {
                    contentType = type,
                    status = "Published",
                    data = new Dictionary<string, object> { ["Phone"] = "second" },
                },
            },
        };

        await ApplyOkAsync(admin, document);

        var exported = await ExportAsync(admin);

        var definition = exported.GetProperty("contentTypes").EnumerateObject().Should().ContainSingle().Subject;
        definition.Value.GetProperty("isSingleton").GetBoolean().Should().BeTrue(
            "a flag that does not survive export turns the settings type back into a list on the "
            + "other side of a migration");

        var entries = exported.GetProperty("contents").EnumerateObject().ToList();
        entries.Should().HaveCount(2,
            "an apply of entries is a restore: it lands what the document holds and is not subject to the cap");
        entries.Select(c => c.Value.GetProperty("data").GetProperty("Phone").GetString())
            .Should().BeEquivalentTo(["first", "second"]);
    }

    /// <summary>
    /// Applying the same document twice names the type by its name rather than leaving two of it.
    /// </summary>
    /// <remarks>
    /// Reapplying is the ordinary thing to do when the first run half worked, so duplicating the
    /// schema on the second pass would punish exactly the person already having a bad day.
    /// </remarks>
    [Fact]
    public async Task Applying_the_same_document_twice_does_not_duplicate_the_content_type()
    {
        var tenant = await TenantAsync();
        var admin = await AdminOfAsync(tenant);
        var type = $"twice{Guid.NewGuid():n}"[..12];

        var (first, firstResult) = await ConfigCallers.ApplyAsync(admin, Document(type, "First"));
        first.StatusCode.Should().Be(HttpStatusCode.OK, firstResult.ToString());
        ConfigCallers.Changes(firstResult).Should().Contain($"contentTypes/{type} create");

        var (planned, plan) = await ConfigCallers.PlanAsync(admin, Document(type, "Second"));
        planned.StatusCode.Should().Be(HttpStatusCode.OK, plan.ToString());
        ConfigCallers.Changes(plan).Should().NotContain($"contentTypes/{type} create",
            "the documented behaviour is that a type is matched by its name");

        await ApplyOkAsync(admin, Document(type, "Second"));

        var exported = await ExportAsync(admin);
        exported.GetProperty("contentTypes").EnumerateObject().Should().ContainSingle(
            "a second apply keeps the type, it does not add another");
    }

    /// <summary>
    /// The document names no tenant, so the request does. An apply lands in the caller's tenant and
    /// is invisible from anyone else's.
    /// </summary>
    [Fact]
    public async Task An_apply_lands_in_the_calling_tenant_and_nowhere_else()
    {
        var mine = await TenantAsync();
        var theirs = await TenantAsync();
        var type = $"scoped{Guid.NewGuid():n}"[..12];

        var mineAdmin = await AdminOfAsync(mine);
        await ApplyOkAsync(mineAdmin, Document(type, "Mine"));

        var mineExport = await ExportAsync(mineAdmin);
        mineExport.GetProperty("contents").EnumerateObject().Should().ContainSingle("the control: it did arrive somewhere");

        var theirsExport = await ExportAsync(await AdminOfAsync(theirs));

        theirsExport.GetProperty("contentTypes").EnumerateObject().Should().BeEmpty(
            "a document carries no tenant identity, so the only thing keeping it out of another club "
            + "is the tenant of the request that delivered it");
        theirsExport.GetProperty("contents").EnumerateObject().Should().BeEmpty();
    }

    /// <summary>
    /// Export is closed to a caller without the capability, which matters more than it looks: the
    /// document is every content row the tenant holds, in one response.
    /// </summary>
    [Fact]
    public async Task Export_and_apply_are_closed_to_a_caller_without_an_admin_role()
    {
        var tenant = await TenantAsync();
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(
                roles: ["User"],
                additionalClaims: new Dictionary<string, string> { ["tenant"] = tenant }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);

        (await client.GetAsync("/api/config/export?sections=contentTypes,contents", Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "an export is a copy of everything, handed over in one request");

        (await client.PostAsJsonAsync("/api/config/apply", new { document = Document("anything", "x") }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// A truncated file is refused outright rather than applied as far as it parsed.
    /// </summary>
    /// <remarks>
    /// A partial apply is worse than a failed one. It leaves a state nobody designed, and no clear
    /// way back, on a site somebody is in the middle of migrating.
    /// </remarks>
    [Fact]
    public async Task A_truncated_document_is_rejected_and_writes_nothing()
    {
        var tenant = await TenantAsync();
        var admin = await AdminOfAsync(tenant);
        var type = $"cut{Guid.NewGuid():n}"[..12];

        var whole = JsonSerializer.Serialize(new { document = Document(type, "Never Arrives") });
        var truncated = whole[..(whole.Length / 2)];

        var response = await admin.PostAsync(
            "/api/config/apply", new StringContent(truncated, Encoding.UTF8, "application/json"), Ct);

        response.IsSuccessStatusCode.Should().BeFalse(
            "half a file is not a document, and saying so is the only safe answer");

        var stored = await _callers.InTenantAsync(tenant,
            s => s.Query<ContentTypeDefinition>().AnyAsync(d => d.Name == type, Ct));
        stored.Should().BeFalse("nothing may be applied from a document that was refused");
    }
}

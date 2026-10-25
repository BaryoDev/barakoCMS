using System.Net;
using FluentAssertions;
using Marten;
using barakoCMS.Models;
using static BarakoCMS.Tests.Features.Config.ConfigCallers;

namespace BarakoCMS.Tests.Features.Config;

/// <summary>A content type's icon through a config apply: stored sanitised, refused when unusable, updated on re-apply.</summary>
[Collection("Integration1")]
public class ConfigIconTests
{
    private const string Hostile =
        "<svg viewBox=\"0 0 24 24\" onload=\"alert(1)\"><script>alert(1)</script><path d=\"M1 1h22v22H1z\" fill=\"red\"/></svg>";

    private readonly ConfigCallers _callers;

    public ConfigIconTests(IntegrationTestFixture fixture) => _callers = new ConfigCallers(fixture);

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():n}"[..(prefix.Length + 10)];

    private static object Document(string name, ContentTypeIcon? icon)
    {
        var type = Type(name, ("Title", "string"));
        type.Icon = icon;
        return new { version = 1, contentTypes = new Dictionary<string, object> { [name] = type } };
    }

    private async Task<ContentTypeDefinition> StoredAsync(string tenant, string name) =>
        (await _callers.InTenantAsync(tenant, s => s.Query<ContentTypeDefinition>().SingleAsync(t => t.Name == name, Ct)))!;

    [Fact]
    public async Task Applying_a_type_with_an_icon_stores_it_sanitised()
    {
        var tenant = await _callers.TenantAsync();
        var name = Unique("iconic");
        var client = await _callers.SuperAdminAsync(tenant);

        var (response, body) = await ApplyAsync(client, Document(name, new ContentTypeIcon { Svg = Hostile }));

        response.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        var svg = (await StoredAsync(tenant, name)).Icon!.Svg!;
        svg.Should().Contain("<path");
        svg.Should().NotContain("script");
        svg.Should().NotContain("onload");
        svg.Should().NotContain("red");
        (await PlanAsync(client, Document(name, new ContentTypeIcon { Svg = Hostile }))).Body
            .GetProperty("isEmpty").GetBoolean().Should().BeTrue("the sanitised icon is what a re-apply compares");
    }

    [Fact]
    public async Task An_unusable_icon_fails_the_apply_with_a_message_and_stores_nothing()
    {
        var tenant = await _callers.TenantAsync();
        var name = Unique("iconic");

        var (response, body) = await ApplyAsync(await _callers.SuperAdminAsync(tenant),
            Document(name, new ContentTypeIcon { Svg = "<svg><path d=\"M0 0h5v5z\"/></svg>" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body.ToString());
        body.ToString().Should().Contain("viewBox");
        (await _callers.InTenantAsync(tenant, s => s.Query<ContentTypeDefinition>().CountAsync(t => t.Name == name, Ct)))
            .Should().Be(0);
    }

    [Fact]
    public async Task Re_applying_with_a_changed_icon_updates_it_and_a_document_without_one_keeps_it()
    {
        var tenant = await _callers.TenantAsync();
        var name = Unique("iconic");
        var client = await _callers.SuperAdminAsync(tenant);
        (await ApplyAsync(client, Document(name, new ContentTypeIcon { Name = "home" }))).Response.IsSuccessStatusCode.Should().BeTrue();

        var (response, body) = await ApplyAsync(client, Document(name, new ContentTypeIcon { Name = "calendar" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        (await StoredAsync(tenant, name)).Icon!.Name.Should().Be("calendar");

        (await ApplyAsync(client, Document(name, null))).Response.IsSuccessStatusCode.Should().BeTrue();
        (await StoredAsync(tenant, name)).Icon!.Name.Should().Be("calendar");
    }
}

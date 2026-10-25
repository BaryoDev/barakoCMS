using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// A content type's icon over HTTP: set with PUT, stored sanitised, read back from the type list
/// and, for a publicly deliverable type, from the public type description.
/// </summary>
[Collection("Integration1")]
public class ContentTypeIconTests : IAsyncLifetime
{
    private const string Hostile =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" viewBox=\"0 0 24 24\" onload=\"alert(1)\">"
        + "<script>alert(1)</script><style>path{fill:red}</style>"
        + "<path d=\"M1 1h22v22H1z\" xlink:href=\"https://evil.example/x\"/></svg>";

    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;
    private readonly HttpClient _anon;

    public ContentTypeIconTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
        _anon = fixture.CreateClient();
    }

    public async ValueTask InitializeAsync() =>
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", await _fixture.StoredUserTokenAsync("Admin", "SuperAdmin"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> StoreTypeAsync(bool deliverable = false, ContentTypeIcon? icon = null)
    {
        var name = "icon-" + Guid.NewGuid().ToString("n")[..12];
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = name, DisplayName = name, IsPubliclyDeliverable = deliverable, Icon = icon,
            Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" }],
        });
        await session.SaveChangesAsync(Ct);
        return name;
    }

    private async Task<ContentTypeDefinition?> ReadAsync(string type)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type, Ct);
    }

    private Task<HttpResponseMessage> PutIconAsync(string type, object body) =>
        _client.PutAsJsonAsync($"/api/content-types/{type}/icon", body, Ct);

    private async Task<JsonElement> ListedAsync(string name)
    {
        for (var page = 1; page <= 50; page++)
        {
            var res = await _client.GetAsync($"/api/content-types?page={page}&pageSize=100", Ct);
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));
            var items = doc.RootElement.GetProperty("items");
            if (items.GetArrayLength() == 0) break;
            foreach (var type in items.EnumerateArray())
                if (type.GetProperty("name").GetString() == name) return type.Clone();
        }

        throw new Xunit.Sdk.XunitException($"The type '{name}' is not in the list.");
    }

    [Fact]
    public async Task A_hostile_svg_is_stored_without_the_script_handler_style_and_external_href_and_reads_back_clean()
    {
        var type = await StoreTypeAsync();

        var res = await PutIconAsync(type, new { icon = new { svg = Hostile } });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));

        var stored = (await ReadAsync(type))!.Icon!.Svg!;
        stored.Should().Contain("<path");
        stored.Should().NotContain("script");
        stored.Should().NotContain("onload");
        stored.Should().NotContain("style");
        stored.Should().NotContain("evil.example");
        stored.Should().NotContain("href");

        var listed = (await ListedAsync(type)).GetProperty("icon").GetProperty("svg").GetString();
        listed.Should().Be(stored);
        (await res.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("icon").GetProperty("svg").GetString().Should().Be(stored);
    }

    [Fact]
    public async Task An_svg_with_no_viewBox_is_a_400_naming_the_viewBox_and_stores_nothing()
    {
        var type = await StoreTypeAsync();

        var res = await PutIconAsync(type, new { icon = new { svg = "<svg xmlns=\"http://www.w3.org/2000/svg\"><path d=\"M0 0h5v5z\"/></svg>" } });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("viewBox");
        (await ReadAsync(type))!.Icon.Should().BeNull();
    }

    [Fact]
    public async Task An_svg_with_no_shape_or_that_is_not_XML_is_a_400()
    {
        var type = await StoreTypeAsync();

        var noShape = await PutIconAsync(type, new { icon = new { svg = "<svg viewBox=\"0 0 24 24\"><g/></svg>" } });
        var notXml = await PutIconAsync(type, new { icon = new { svg = "not markup at all" } });

        noShape.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await noShape.Content.ReadAsStringAsync(Ct)).Should().Contain("no shape");
        notXml.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await notXml.Content.ReadAsStringAsync(Ct)).Should().Contain("XML");
    }

    [Fact]
    public async Task A_sprite_name_is_stored_and_null_clears_it()
    {
        var type = await StoreTypeAsync();

        (await PutIconAsync(type, new { icon = new { name = "calendar-event" } })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(type))!.Icon!.Name.Should().Be("calendar-event");

        (await PutIconAsync(type, new { icon = (object?)null })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(type))!.Icon.Should().BeNull();
    }

    [Fact]
    public async Task A_bad_sprite_name_and_an_unknown_type_are_refused()
    {
        var type = await StoreTypeAsync();

        (await PutIconAsync(type, new { icon = new { name = "not a name" } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PutIconAsync($"nosuch-{Guid.NewGuid():n}", new { icon = new { name = "home" } })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Creating_a_type_with_an_icon_stores_it_sanitised()
    {
        var name = "icon-" + Guid.NewGuid().ToString("n")[..12];

        var created = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name,
            displayName = "Iconic",
            icon = new { svg = Hostile },
            fields = new object[] { new { name = "Title", displayName = "Title", type = "string" } },
        }, Ct);
        created.IsSuccessStatusCode.Should().BeTrue(await created.Content.ReadAsStringAsync(Ct));

        var stored = (await ReadAsync(name))!.Icon!.Svg!;
        stored.Should().Contain("<path");
        stored.Should().NotContain("script");

        var refused = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name = name + "x",
            displayName = "Iconic",
            icon = new { svg = "<svg><path d=\"M0 0\"/></svg>" },
            fields = new object[] { new { name = "Title", displayName = "Title", type = "string" } },
        }, Ct);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_definition_stored_before_icons_existed_loads_with_icon_null()
    {
        var old = "{\"Id\":\"" + Guid.NewGuid() + "\",\"Name\":\"old\",\"DisplayName\":\"Old\",\"Fields\":[]}";
        System.Text.Json.JsonSerializer.Deserialize<ContentTypeDefinition>(old)!.Icon.Should().BeNull();
        Newtonsoft.Json.JsonConvert.DeserializeObject<ContentTypeDefinition>(old)!.Icon.Should().BeNull();

        var type = await StoreTypeAsync();

        (await ReadAsync(type))!.Icon.Should().BeNull();
        (await ListedAsync(type)).GetProperty("icon").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task The_public_type_description_carries_the_icon_only_for_a_deliverable_type()
    {
        var open = await StoreTypeAsync(deliverable: true, icon: new ContentTypeIcon { Name = "megaphone" });
        var hidden = await StoreTypeAsync(deliverable: false, icon: new ContentTypeIcon { Name = "megaphone" });

        var described = await _anon.GetAsync($"/api/public/types/{open}/description", Ct);
        var refused = await _anon.GetAsync($"/api/public/types/{hidden}/description", Ct);

        described.StatusCode.Should().Be(HttpStatusCode.OK);
        (await described.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("icon").GetProperty("name").GetString().Should().Be("megaphone");
        refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().NotContain("megaphone");
    }

    [Fact]
    public async Task Setting_the_icon_needs_an_admin()
    {
        var type = await StoreTypeAsync();

        var anonymous = await _anon.PutAsJsonAsync($"/api/content-types/{type}/icon", new { icon = new { name = "home" } }, Ct);

        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadAsync(type))!.Icon.Should().BeNull();
    }
}

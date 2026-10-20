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
/// A type that declares lines takes them with its header in one transaction; a type that does not
/// is created as before (#578).
/// </summary>
[Collection("Sequential")]
public class ContentLinesTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;

    public ContentLinesTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
        var ip = Guid.NewGuid().ToByteArray();
        _client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"10.{ip[0]}.{ip[1]}.{ip[2]}");
    }

    public async ValueTask InitializeAsync() =>
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", await _fixture.StoredUserTokenAsync("Admin", "SuperAdmin"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewName(string prefix) => prefix + Guid.NewGuid().ToString("n")[..10];

    private async Task<(string Header, string Line)> TypesAsync(bool declare = true)
    {
        var header = NewName("order");
        var line = NewName("orderline");
        (await _client.PostAsJsonAsync("/api/content-types", new
        {
            name = header, displayName = header,
            fields = new object[] { new { name = "Customer", type = "string" } },
        }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.PostAsJsonAsync("/api/content-types", new
        {
            name = line, displayName = line,
            fields = new object[]
            {
                new { name = "Sku", type = "string", isRequired = true },
                new { name = "Order", type = "reference", referenceType = header },
            },
        }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        if (declare)
        {
            var set = await _client.PutAsJsonAsync($"/api/content-types/{header}/lines",
                new { lines = new { contentType = line, parentField = "order" } }, Ct);
            set.StatusCode.Should().Be(HttpStatusCode.OK, await set.Content.ReadAsStringAsync(Ct));
        }

        return (header, line);
    }

    private async Task<List<Content>> StoredAsync(string type)
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession("*DEFAULT*");
        return (await session.Query<Content>().Where(c => c.ContentType == type).ToListAsync(Ct)).ToList();
    }

    [Fact]
    public async Task A_header_and_its_lines_are_stored_together_and_each_line_points_at_the_header()
    {
        var (header, line) = await TypesAsync();

        var res = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = header,
            data = new { Customer = "Ana" },
            lines = new object[] { new { Sku = "A-1" }, new { Sku = "B-2", Order = Guid.NewGuid().ToString() } },
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var headerId = body.GetProperty("id").GetGuid();
        body.GetProperty("lineIds").GetArrayLength().Should().Be(2);

        var stored = await StoredAsync(line);
        stored.Should().HaveCount(2);
        stored.Select(c => c.Data["Order"].ToString()).Should().OnlyContain(v => v == headerId.ToString(),
            "the server sets the parent field, whatever the line sent there");
        (await StoredAsync(header)).Should().ContainSingle(c => c.Id == headerId);
    }

    [Fact]
    public async Task A_refused_line_stores_neither_the_header_nor_the_lines_before_it()
    {
        var (header, line) = await TypesAsync();

        var res = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = header,
            data = new { Customer = "Ben" },
            lines = new object[] { new { Sku = "A-1" }, new { Note = "no sku" } },
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("Line 2");
        (await StoredAsync(header)).Should().BeEmpty();
        (await StoredAsync(line)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_dry_run_with_lines_stores_nothing()
    {
        var (header, line) = await TypesAsync();

        var res = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = header, dryRun = true,
            data = new { Customer = "Cy" },
            lines = new object[] { new { Sku = "A-1" } },
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        (await StoredAsync(header)).Should().BeEmpty();
        (await StoredAsync(line)).Should().BeEmpty();
    }

    [Fact]
    public async Task Lines_sent_to_a_type_that_declares_none_are_refused()
    {
        var (header, _) = await TypesAsync(declare: false);

        var res = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = header, data = new { Customer = "Di" }, lines = new object[] { new { Sku = "A-1" } },
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StoredAsync(header)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_create_without_lines_is_unchanged_on_a_type_that_declares_them()
    {
        var (header, line) = await TypesAsync();

        var res = await _client.PostAsJsonAsync("/api/contents", new { contentType = header, data = new { Customer = "Ed" } }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Ct);
        (!body.TryGetProperty("lineIds", out var ids) || ids.ValueKind == JsonValueKind.Null).Should().BeTrue();
        (await StoredAsync(header)).Should().ContainSingle();
        (await StoredAsync(line)).Should().BeEmpty();
    }

    [Fact]
    public async Task Declaring_lines_needs_a_single_reference_to_the_header_on_the_line_type()
    {
        var (header, line) = await TypesAsync(declare: false);

        var wrongField = await _client.PutAsJsonAsync($"/api/content-types/{header}/lines",
            new { lines = new { contentType = line, parentField = "sku" } }, Ct);
        var noField = await _client.PutAsJsonAsync($"/api/content-types/{header}/lines",
            new { lines = new { contentType = line, parentField = "nope" } }, Ct);
        var noType = await _client.PutAsJsonAsync($"/api/content-types/{header}/lines",
            new { lines = new { contentType = "missing" + Guid.NewGuid().ToString("n")[..6], parentField = "order" } }, Ct);

        wrongField.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        noField.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        noType.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Clearing_lines_takes_them_off_the_type()
    {
        var (header, _) = await TypesAsync();

        var clear = await _client.PutAsJsonAsync($"/api/content-types/{header}/lines", new { lines = (object?)null }, Ct);
        clear.StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = header, data = new { Customer = "Flo" }, lines = new object[] { new { Sku = "A-1" } },
        }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Lines_are_refused_when_the_line_type_lost_its_parent_field()
    {
        var (header, line) = await TypesAsync();
        using (var scope = _fixture.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            await using var session = store.LightweightSession("*DEFAULT*");
            var def = await session.Query<ContentTypeDefinition>().FirstAsync(d => d.Name == line, Ct);
            def.Fields = def.Fields.Where(f => !string.Equals(f.Name, "Order", StringComparison.OrdinalIgnoreCase)).ToList();
            session.Store(def);
            await session.SaveChangesAsync(Ct);
        }

        var res = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = header, data = new { Customer = "Gus" }, lines = new object[] { new { Sku = "A-1" } },
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StoredAsync(header)).Should().BeEmpty();
        (await StoredAsync(line)).Should().BeEmpty();
    }
}

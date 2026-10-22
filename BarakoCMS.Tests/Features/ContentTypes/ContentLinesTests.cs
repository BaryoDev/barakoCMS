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

    private async Task<(string Header, string Line)> TotalTypesAsync(bool declare = true)
    {
        var header = NewName("invoice");
        var line = NewName("invoiceline");
        (await _client.PostAsJsonAsync("/api/content-types", new
        {
            name = header, displayName = header,
            fields = new object[] { new { name = "Total", type = "decimal" } },
        }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.PostAsJsonAsync("/api/content-types", new
        {
            name = line, displayName = line,
            fields = new object[]
            {
                new { name = "Amount", type = "decimal" },
                new { name = "Invoice", type = "reference", referenceType = header },
            },
        }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        if (declare)
        {
            var set = await _client.PutAsJsonAsync($"/api/content-types/{header}/lines",
                new { lines = new { contentType = line, parentField = "invoice", totalField = "total", lineAmountField = "amount" } }, Ct);
            set.StatusCode.Should().Be(HttpStatusCode.OK, await set.Content.ReadAsStringAsync(Ct));
        }

        return (header, line);
    }

    [Fact]
    public async Task A_total_that_is_not_the_sum_of_its_lines_stores_nothing()
    {
        var (header, line) = await TotalTypesAsync();

        var res = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = header,
            data = new { Total = 100 },
            lines = new object[] { new { Amount = 60 }, new { Amount = 30 } },
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("add up to 90");
        (await StoredAsync(header)).Should().BeEmpty();
        (await StoredAsync(line)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_total_that_is_the_sum_of_its_lines_is_stored()
    {
        var (header, line) = await TotalTypesAsync();

        var res = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = header,
            data = new { Total = 90.5 },
            lines = new object[] { new { Amount = 60.25 }, new { Amount = 30.25 } },
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        (await StoredAsync(line)).Should().HaveCount(2);
        var stored = await StoredAsync(header);
        stored.Should().ContainSingle();
        decimal.Parse(stored[0].Data["Total"].ToString()!, System.Globalization.CultureInfo.InvariantCulture).Should().Be(90.5m);
    }

    [Fact]
    public async Task Declaring_a_total_without_its_line_amount_is_refused()
    {
        var (header, line) = await TotalTypesAsync(declare: false);

        var half = await _client.PutAsJsonAsync($"/api/content-types/{header}/lines",
            new { lines = new { contentType = line, parentField = "invoice", totalField = "total" } }, Ct);
        var both = await _client.PutAsJsonAsync($"/api/content-types/{header}/lines",
            new { lines = new { contentType = line, parentField = "invoice", totalField = "total", lineAmountField = "amount" } }, Ct);

        half.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await half.Content.ReadAsStringAsync(Ct)).Should().Contain("together");
        both.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_header_with_a_total_and_no_lines_is_held_to_zero()
    {
        var (header, _) = await TotalTypesAsync();

        var refused = await _client.PostAsJsonAsync("/api/contents", new { contentType = header, data = new { Total = 5 } }, Ct);
        var emptyList = await _client.PostAsJsonAsync("/api/contents",
            new { contentType = header, data = new { Total = 5 }, lines = Array.Empty<object>() }, Ct);
        var accepted = await _client.PostAsJsonAsync("/api/contents", new { contentType = header, data = new { Total = 0 } }, Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        emptyList.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        accepted.StatusCode.Should().Be(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync(Ct));
        (await StoredAsync(header)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_line_with_no_amount_or_a_non_number_is_refused_by_name()
    {
        var (header, line) = await TotalTypesAsync();

        var missing = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = header, data = new { Total = 10 }, lines = new object[] { new { Amount = 10 }, new { } },
        }, Ct);
        var text = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = header, data = new { Total = 10 }, lines = new object[] { new { Amount = 10 }, new { Amount = "abc" } },
        }, Ct);
        var fine = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = header, data = new { Total = 10 }, lines = new object[] { new { Amount = 10 } },
        }, Ct);

        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await missing.Content.ReadAsStringAsync(Ct)).Should().Contain("Line 2").And.Contain("Amount");
        text.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fine.StatusCode.Should().Be(HttpStatusCode.OK, await fine.Content.ReadAsStringAsync(Ct));
        (await StoredAsync(line)).Should().ContainSingle();
    }

    [Fact]
    public async Task Amounts_in_exponent_form_are_added_and_amounts_too_large_to_add_are_refused_not_crashed()
    {
        var (header, line) = await TotalTypesAsync();

        var exponent = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = header, data = new { Total = "1E2" }, lines = new object[] { new { Amount = "6E1" }, new { Amount = "4E1" } },
        }, Ct);
        var overflow = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = header, data = new { Total = 1 },
            lines = new object[] { new { Amount = decimal.MaxValue }, new { Amount = decimal.MaxValue } },
        }, Ct);

        exponent.StatusCode.Should().Be(HttpStatusCode.OK, await exponent.Content.ReadAsStringAsync(Ct));
        overflow.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StoredAsync(line)).Should().HaveCount(2, "only the exponent-form invoice was stored");
    }

    private async Task<(Guid Header, List<Guid> Lines)> InvoiceAsync(string header)
    {
        var res = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = header, data = new { Total = 100 }, lines = new object[] { new { Amount = 60 }, new { Amount = 40 } },
        }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return (body.GetProperty("id").GetGuid(), body.GetProperty("lineIds").EnumerateArray().Select(e => e.GetGuid()).ToList());
    }

    [Fact]
    public async Task Updating_a_header_to_a_total_its_lines_do_not_add_up_to_is_refused()
    {
        var (header, _) = await TotalTypesAsync();
        var (id, _) = await InvoiceAsync(header);

        var refused = await _client.PutAsJsonAsync($"/api/contents/{id}", new { data = new { Total = 150 } }, Ct);
        var control = await _client.PutAsJsonAsync($"/api/contents/{id}", new { data = new { Total = 100 } }, Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("add up to 100");
        control.StatusCode.Should().Be(HttpStatusCode.OK, await control.Content.ReadAsStringAsync(Ct));
        decimal.Parse((await StoredAsync(header)).Single().Data["Total"].ToString()!, System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(100m);
    }

    [Fact]
    public async Task Updating_a_line_so_its_header_total_no_longer_holds_is_refused()
    {
        var (header, line) = await TotalTypesAsync();
        var (id, lineIds) = await InvoiceAsync(header);
        var stored = (await StoredAsync(line)).Single(c => c.Id == lineIds[0]);
        var data = new Dictionary<string, object>(stored.Data, StringComparer.OrdinalIgnoreCase);

        data["Amount"] = 70;
        var refused = await _client.PutAsJsonAsync($"/api/contents/{lineIds[0]}", new { data }, Ct);
        data["Amount"] = 60;
        var control = await _client.PutAsJsonAsync($"/api/contents/{lineIds[0]}", new { data }, Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain(id.ToString());
        control.StatusCode.Should().Be(HttpStatusCode.OK, await control.Content.ReadAsStringAsync(Ct));
        (await StoredAsync(line)).Single(c => c.Id == lineIds[0]).Data["Amount"].ToString().Should().Be("60");
    }

    [Fact]
    public async Task The_lines_of_a_total_hold_their_fields_against_a_rename()
    {
        var (header, line) = await TotalTypesAsync();

        var lineAmount = await _client.PostAsJsonAsync($"/api/content-types/{line}/fields/Amount/change",
            new { newName = "Price" }, Ct);
        var total = await _client.PostAsJsonAsync($"/api/content-types/{header}/fields/Total/change",
            new { remove = true, allowDataLoss = true }, Ct);

        lineAmount.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await lineAmount.Content.ReadAsStringAsync(Ct)).Should().Contain("lines");
        total.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await total.Content.ReadAsStringAsync(Ct)).Should().Contain("lines total");
    }

    [Fact]
    public async Task A_header_is_read_with_only_its_own_lines_in_one_response()
    {
        var (header, line) = await TypesAsync();
        var first = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = header, data = new { Customer = "Gus" },
            lines = new object[] { new { Sku = "A-1" }, new { Sku = "B-2" }, new { Sku = "C-3" } },
        }, Ct);
        var other = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = header, data = new { Customer = "Hal" }, lines = new object[] { new { Sku = "Z-9" } },
        }, Ct);
        var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        await other.Content.ReadAsStringAsync(Ct);

        var res = await _client.GetAsync($"/api/contents/{firstId}/lines", Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("header").GetProperty("id").GetGuid().Should().Be(firstId);
        var skus = body.GetProperty("lines").EnumerateArray()
            .Select(l => l.GetProperty("data").EnumerateObject().First(p => p.NameEquals("Sku") || p.NameEquals("sku")).Value.GetString())
            .ToList();
        skus.Should().HaveCount(3);
        skus.Should().BeEquivalentTo(["A-1", "B-2", "C-3"]);
    }

    [Fact]
    public async Task Another_tenant_cannot_read_a_header_or_its_lines()
    {
        var (header, line) = await TypesAsync();
        var created = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = header, data = new { Customer = "Ivy" }, lines = new object[] { new { Sku = "A-1" } },
        }, Ct);
        var headerId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        var slug = "scope" + Guid.NewGuid().ToString("n")[..10];
        var userId = Guid.NewGuid();
        using (var scope = _fixture.Services.CreateScope())
        {
            await using var session = scope.ServiceProvider.GetRequiredService<IDocumentStore>().LightweightSession();
            session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
            session.Store(new User { Id = userId, Username = slug, Email = slug + "@example.com" });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(), UserId = userId, TenantSlug = slug,
                Status = MembershipStatus.Active, RoleIds = [SystemRoles.AdminRoleId],
            });
            await session.SaveChangesAsync(Ct);
        }

        var other = _fixture.CreateClient();
        other.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
            _fixture.CreateToken(
                roles: ["Admin"], userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = slug, ["Username"] = slug }));
        other.DefaultRequestHeaders.Add("X-Tenant", slug);
        other.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, "198.51.100.77");

        (await other.GetAsync($"/api/contents/{headerId}/lines", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the header belongs to the default tenant, and the read goes through a tenant-filtered session");
        (await _client.GetAsync($"/api/contents/{headerId}/lines", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await StoredAsync(line)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_line_stored_in_another_tenant_pointing_at_the_header_is_not_read_with_it()
    {
        var (header, line) = await TypesAsync();
        var created = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = header, data = new { Customer = "Jo" }, lines = new object[] { new { Sku = "OWN-1" } },
        }, Ct);
        var headerId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        var foreign = Guid.NewGuid();
        using (var scope = _fixture.Services.CreateScope())
        {
            await using var session = scope.ServiceProvider.GetRequiredService<IDocumentStore>()
                .LightweightSession("tenant-b-" + Guid.NewGuid().ToString("n")[..8]);
            session.Store(new Content
            {
                Id = foreign, ContentType = line,
                Data = new Dictionary<string, object> { ["Sku"] = "FOREIGN-1", ["Order"] = headerId.ToString() },
            });
            await session.SaveChangesAsync(Ct);
        }

        var res = await _client.GetAsync($"/api/contents/{headerId}/lines", Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var ids = body.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("id").GetGuid()).ToList();
        ids.Should().HaveCount(1, "the caller's own line is present");
        ids.Should().NotContain(foreign, "a line of another tenant is never part of this tenant's header");
        body.GetProperty("total").GetInt32().Should().Be(1);
    }
}

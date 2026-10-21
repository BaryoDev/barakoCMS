using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using static BarakoCMS.Tests.Features.Config.ConfigCallers;

namespace BarakoCMS.Tests.Numbering;

/// <summary>A number a client took from a client-allocated series is kept and recorded; any other is replaced.</summary>
[Collection("Sequential")]
public class ClientAllocatedCreateTests(IntegrationTestFixture fixture)
{
    private readonly BarakoCMS.Tests.Features.Config.ConfigCallers callers = new(fixture);

    private async Task<HttpClient> CustomerClientAsync(bool clientAllocated)
    {
        var tenant = await callers.TenantAsync();
        await callers.StoreInTenantAsync(tenant, Type("Customer", ("CustomerNo", "string"), ("Name", "string")));
        var client = await callers.SuperAdminAsync(tenant);
        (await client.PutAsJsonAsync("/api/content-types/Customer/numbering", new
        {
            field = "CustomerNo", prefix = "P-", padding = 4, clientAllocated,
        }, Ct)).EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>Takes <paramref name="count"/> numbers from the next-number route, as a client would.</summary>
    private static async Task AllocateAsync(HttpClient client, int count)
    {
        for (var i = 0; i < count; i++)
        {
            (await client.PostAsJsonAsync("/api/numbering/next", new { contentType = "Customer" }, Ct)).EnsureSuccessStatusCode();
        }
    }

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string number) =>
        client.PostAsJsonAsync("/api/contents", new { contentType = "Customer", data = new { Name = "A customer", CustomerNo = number } }, Ct);

    private static async Task<string> StoredNumberAsync(HttpClient client, HttpResponseMessage created)
    {
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("id").GetGuid();
        var entry = JsonDocument.Parse(await client.GetStringAsync($"/api/contents/{id}", Ct)).RootElement;
        return entry.GetProperty("data").GetProperty("CustomerNo").GetString()!;
    }

    [Fact]
    public async Task A_number_of_a_client_allocated_series_is_kept_and_recorded()
    {
        var client = await CustomerClientAsync(clientAllocated: true);
        await AllocateAsync(client, 7);

        var created = await CreateAsync(client, "P-0007");

        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync(Ct));
        (await StoredNumberAsync(client, created)).Should().Be("P-0007");
        var gaps = JsonDocument.Parse(await client.GetStringAsync("/api/numbering/external/type.customer/gaps?from=1&to=7", Ct)).RootElement;
        gaps.GetProperty("missing").EnumerateArray().Select(r => (r.GetProperty("from").GetInt64(), r.GetProperty("to").GetInt64()))
            .Should().Equal((1L, 6L));
    }

    [Fact]
    public async Task Without_the_flag_the_server_replaces_the_number()
    {
        var client = await CustomerClientAsync(clientAllocated: false);

        var created = await CreateAsync(client, "P-0007");

        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync(Ct));
        (await StoredNumberAsync(client, created)).Should().Be("P-0001");
    }

    [Fact]
    public async Task A_number_that_is_already_issued_is_refused()
    {
        var client = await CustomerClientAsync(clientAllocated: true);
        await AllocateAsync(client, 7);
        (await CreateAsync(client, "P-0007")).EnsureSuccessStatusCode();

        var again = await CreateAsync(client, "P-0007");

        again.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await again.Content.ReadAsStringAsync(Ct)).Should().Contain("That number is already issued.");
    }

    [Fact]
    public async Task A_number_that_does_not_fit_the_series_is_refused()
    {
        var client = await CustomerClientAsync(clientAllocated: true);

        var response = await CreateAsync(client, "X-1");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("That number does not fit the series.");
    }

    [Fact]
    public async Task A_batch_step_with_a_number_that_does_not_fit_is_400_and_writes_nothing()
    {
        var client = await CustomerClientAsync(clientAllocated: true);
        await AllocateAsync(client, 3);

        var response = await client.PostAsJsonAsync("/api/contents/batch", new
        {
            steps = new object[]
            {
                new { op = "create", contentType = "Customer", id = Guid.CreateVersion7(), data = new { Name = "A customer", CustomerNo = "P-0003" } },
                new { op = "create", contentType = "Customer", id = Guid.CreateVersion7(), data = new { Name = "B customer", CustomerNo = "X-1" } },
            },
        }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a numbering refusal is the caller's input, not a server fault");
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("That number does not fit the series.");
        var list = JsonDocument.Parse(await client.GetStringAsync("/api/contents?contentType=Customer", Ct)).RootElement;
        list.GetProperty("items").GetArrayLength().Should().Be(0, "the batch is all or nothing");
    }

    [Fact]
    public async Task An_import_row_with_a_number_that_does_not_fit_is_a_row_error()
    {
        var client = await CustomerClientAsync(clientAllocated: true);
        await AllocateAsync(client, 3);

        var response = await client.PostAsJsonAsync("/api/import/content", new
        {
            contentType = "Customer",
            records = new[]
            {
                new Dictionary<string, object> { ["Name"] = "A customer", ["CustomerNo"] = "P-0003" },
                new Dictionary<string, object> { ["Name"] = "B customer", ["CustomerNo"] = "X-1" },
            },
        }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        var error = body.GetProperty("errors").EnumerateArray().Should().ContainSingle().Subject;
        error.GetProperty("row").GetInt32().Should().Be(1);
        error.GetRawText().Should().Contain("That number does not fit the series.");
    }

    [Theory]
    [InlineData("P-00007")]
    [InlineData("P-007")]
    public async Task A_longer_or_shorter_spelling_of_an_issued_number_is_refused_cleanly(string spelling)
    {
        var client = await CustomerClientAsync(clientAllocated: true);
        await AllocateAsync(client, 7);
        (await CreateAsync(client, "P-0007")).EnsureSuccessStatusCode();

        var again = await CreateAsync(client, spelling);

        again.StatusCode.Should().Be(HttpStatusCode.BadRequest, await again.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_batch_refuses_a_number_that_is_already_issued_and_one_it_uses_twice()
    {
        var client = await CustomerClientAsync(clientAllocated: true);
        await AllocateAsync(client, 2);
        (await CreateAsync(client, "P-0001")).EnsureSuccessStatusCode();

        var issued = await client.PostAsJsonAsync("/api/contents/batch", new
        {
            steps = new object[] { new { op = "create", contentType = "Customer", id = Guid.CreateVersion7(), data = new { Name = "A", CustomerNo = "P-0001" } } },
        }, Ct);
        var twice = await client.PostAsJsonAsync("/api/contents/batch", new
        {
            steps = new object[]
            {
                new { op = "create", contentType = "Customer", id = Guid.CreateVersion7(), data = new { Name = "A", CustomerNo = "P-0002" } },
                new { op = "create", contentType = "Customer", id = Guid.CreateVersion7(), data = new { Name = "B", CustomerNo = "P-00002" } },
            },
        }, Ct);

        issued.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await issued.Content.ReadAsStringAsync(Ct)).Should().Contain("That number is already issued.");
        twice.StatusCode.Should().Be(HttpStatusCode.BadRequest, await twice.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task An_import_row_with_a_number_that_is_already_issued_is_a_row_error()
    {
        var client = await CustomerClientAsync(clientAllocated: true);
        await AllocateAsync(client, 2);
        (await CreateAsync(client, "P-0001")).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync("/api/import/content", new
        {
            contentType = "Customer",
            records = new[]
            {
                new Dictionary<string, object> { ["Name"] = "A customer", ["CustomerNo"] = "P-0002" },
                new Dictionary<string, object> { ["Name"] = "B customer", ["CustomerNo"] = "P-0001" },
            },
        }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        var error = body.GetProperty("errors").EnumerateArray().Should().ContainSingle().Subject;
        error.GetProperty("row").GetInt32().Should().Be(1);
        error.GetRawText().Should().Contain("That number is already issued.");
    }

    [Fact]
    public async Task A_number_ahead_of_what_the_route_handed_out_is_refused()
    {
        var client = await CustomerClientAsync(clientAllocated: true);
        await AllocateAsync(client, 2);

        var ahead = await CreateAsync(client, "P-0003");

        ahead.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ahead.Content.ReadAsStringAsync(Ct)).Should().Contain("That number was not handed out.");
        var next = JsonDocument.Parse(await (await client.PostAsJsonAsync("/api/numbering/next", new { contentType = "Customer" }, Ct))
            .Content.ReadAsStringAsync(Ct)).RootElement;
        next.GetRawText().Should().Contain("P-0003", "the refused number stays free for the route to hand out");
    }
}
